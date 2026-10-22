"""Exercise credential-safe output and failure paths with real subprocesses."""

from contextlib import redirect_stderr
from io import StringIO
import base64
import os
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.parse

import guard
import signing


class CaptureTests(unittest.TestCase):
    def capture(self, code, env=None):
        with tempfile.TemporaryDirectory() as temp:
            logs = Path(temp)
            with patch.object(guard.sys, "stdout") as stdout, patch.object(guard.sys, "stderr"):
                with signing.session_signals() as interrupted:
                    status = guard.capture([sys.executable, "-c", code], env or dict(os.environ),
                                           guard.secret_forms(env or {}), logs, interrupted)
                artifacts = b"".join(path.read_bytes() for path in logs.glob("*.log"))
                console = b"".join(call.args[0] for call in stdout.buffer.write.call_args_list)
            return status, artifacts, console

    def test_encoded_chunked_secrets_are_redacted_and_status_preserved(self):
        secret = 'token with <> & "quotes"'
        forms = [secret, base64.b64encode(secret.encode()).decode(), urllib.parse.quote(secret)]
        code = ("import os, sys, time\n"
                "for value in " + repr(forms) + ":\n"
                " data = value.encode(); os.write(1, data[:7]); time.sleep(.01); os.write(1, data[7:] + b'\\n')\n"
                " os.write(2, data + b'\\n')\n"
                "sys.exit(23)\n")
        status, logs, console = self.capture(code, {**os.environ, "NUGET_PUBLISH_TOKEN": secret})
        self.assertEqual(status, 23)
        for value in forms:
            self.assertNotIn(value.encode(), logs + console)
        self.assertIn(b"[REDACTED]", logs)

    def test_overflow_never_prints_partial_secret(self):
        with patch.object(guard, "LIMIT", 64):
            status, logs, console = self.capture("import os; os.write(1, b'x' * 1024); raise SystemExit(7)")
        self.assertEqual(status, 7)
        self.assertNotIn(b"x" * 64, logs + console)
        self.assertIn(guard.OVERFLOW_LOG, logs)

    def test_sanitizer_failure_preserves_child_status_without_raw_output(self):
        with patch.object(guard, "redact", side_effect=ValueError("unsafe payload")):
            status, logs, console = self.capture("print('unsafe payload'); raise SystemExit(19)")
        self.assertEqual(status, 19)
        self.assertNotIn(b"unsafe payload", logs + console)
        self.assertIn(guard.FAILED_LOG, logs)

    def test_unwritable_logs_preserve_child_status(self):
        with tempfile.TemporaryDirectory() as temp:
            logs = Path(temp) / "regular-file"
            logs.write_text("occupied")
            with patch.object(guard.sys, "stdout"), patch.object(guard.sys, "stderr"):
                with signing.session_signals() as interrupted:
                    status = guard.capture([sys.executable, "-c", "raise SystemExit(11)"],
                                           os.environ, [], logs, interrupted)
            self.assertEqual(status, 11)
            self.assertEqual(logs.read_text(), "occupied")

    def test_incomplete_stream_is_withheld(self):
        code = ("import subprocess, sys\n"
                "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(.4)'])\n"
                "print('partial-unsafe-stream', flush=True)\n")
        with patch.object(guard, "DRAIN_GRACE", 0.01):
            status, logs, console = self.capture(code)
        self.assertEqual(status, 0)
        self.assertNotIn(b"partial-unsafe-stream", logs + console)
        self.assertIn(guard.INCOMPLETE_LOG, logs)


class PolicyTests(unittest.TestCase):
    def test_publication_requires_workflow_token_without_legacy_fallback(self):
        for env in ({"GITHUB_ACTIONS": "true", "NUGET_API_KEY": "legacy-key"},
                    {"GITHUB_ACTIONS": "true", "NUGET_PUBLISH_TOKEN": ""},
                    {"GITHUB_ACTIONS": "false", "NUGET_PUBLISH_TOKEN": "temporary-token"},
                    {"GITHUB_ACTIONS": "true", "NUGET_PUBLISH_TOKEN": "temporary-token",
                     "NUGET_SOURCE": "https://other.example/index.json"}):
            with self.subTest(env=env), patch.dict(os.environ, env, clear=True), \
                    patch.object(sys, "argv", ["guard.py", "publish"]), \
                    patch.object(guard, "authorize_operator"), \
                    patch.object(guard, "verify_outputs", return_value=[Path("test.nupkg")]), \
                    patch.object(guard, "capture") as capture, redirect_stderr(StringIO()):
                self.assertEqual(guard.main(), 2)
                capture.assert_not_called()

    def test_workflow_uses_oidc_only_for_publication(self):
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/release.yml").read_text()
        self.assertIn("      id-token: write\n", workflow)
        login = workflow.split("      - name: Authenticate NuGet trusted publisher\n")[1].split("      - name:")[0]
        self.assertIn("if: inputs.dry-run == false", login)
        self.assertIn("uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841", login)
        self.assertIn("user: scylladb.publish.code", login)
        self.assertNotIn("secrets.NUGET_API_KEY", workflow)
        self.assertEqual(workflow.count("steps.nuget-login.outputs.NUGET_API_KEY"), 1)
        publish = workflow.split("      - name: Perform release\n")[1].split("      - name:")[0]
        self.assertIn("NUGET_PUBLISH_TOKEN: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}", publish)
        self.assertIn("if: inputs.dry-run == false", publish)
        self.assertLess(workflow.index("Recheck for open release blockers"),
                        workflow.index("Authenticate NuGet trusted publisher"))

    def test_unsafe_inputs_are_rejected_without_echoing_values(self):
        for name, value in [("RELEASE_VERSION", '1.0.0$(touch unsafe)'),
                            ("PACKAGE_OUTPUT_DIR", 'nupkgs; touch unsafe'),
                            ("NUGET_SOURCE", 'https://user:secret@example.invalid/index.json'),
                            ("DOTNET_VERBOSITY", 'diagnostic'), ("MSBUILDDEBUGENGINE", '1')]:
            with self.subTest(name=name), patch.dict(os.environ, {name: value}, clear=True):
                output = StringIO()
                with patch.object(sys, "argv", ["guard.py", "run", "unused"]), redirect_stderr(output):
                    self.assertEqual(guard.main(), 2)
                self.assertNotIn(value, output.getvalue())

    def test_debug_and_credential_options_are_rejected(self):
        for args in (["--verbosity", "diagnostic"], ["-v:diag"], ["/bl:build.binlog"],
                     ["/fl"], ["/p:Password=private"], ["--api-key", "private"]):
            with self.subTest(args=args), self.assertRaises(guard.Unsafe):
                guard.check_command(["dotnet", "build", *args])
        guard.check_command(["dotnet", "restore", "--verbosity", "minimal"])

    def test_protected_environment_fails_closed_on_weaker_settings(self):
        environment = {"can_admins_bypass": False,
                       "protection_rules": [{"type": "required_reviewers", "prevent_self_review": True,
                                             "reviewers": [{"type": "Team"}]}],
                       "deployment_branch_policy": {"protected_branches": False, "custom_branch_policies": True}}
        branches = {"total_count": 1, "branch_policies": [{"name": "main", "type": "branch"}]}
        with patch.dict(os.environ, {"GITHUB_REPOSITORY": "owner/repo"}, clear=True):
            with patch.object(signing, "run", side_effect=[json.dumps(environment), json.dumps(branches)]):
                guard.check_environment()
            invalid = []
            for name, value in [("can_admins_bypass", True), ("protection_rules", []),
                                ("deployment_branch_policy", None)]:
                candidate = copy.deepcopy(environment)
                candidate[name] = value
                invalid.append(candidate)
            for name in ("prevent_self_review", "reviewers"):
                candidate = copy.deepcopy(environment)
                candidate["protection_rules"][0][name] = False if name == "prevent_self_review" else []
                invalid.append(candidate)
            for candidate in invalid:
                with self.subTest(environment=candidate), \
                        patch.object(signing, "run", return_value=json.dumps(candidate)), self.assertRaises(guard.Unsafe):
                    guard.check_environment()
            for branch in [{"total_count": 0, "branch_policies": []},
                           {"total_count": 1, "branch_policies": [{"name": "*", "type": "branch"}]},
                           {"total_count": 1, "branch_policies": [{"name": "main", "type": "tag"}]}]:
                with self.subTest(branch=branch), patch.object(signing, "run", side_effect=[json.dumps(environment), json.dumps(branch)]), \
                        self.assertRaises(guard.Unsafe):
                    guard.check_environment()
            with patch.object(signing, "run", side_effect=signing.SigningError("missing environment")), \
                    self.assertRaises(signing.SigningError):
                guard.check_environment()

    def test_direct_guard_cannot_bypass_local_operator_policy(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}, clear=True), \
                patch.object(signing, "run", return_value="other-maintainer"), self.assertRaises(guard.Unsafe):
            guard.authorize_operator()

    def test_stderr_cannot_poison_parsed_stdout(self):
        result = subprocess.CompletedProcess([], 0, "expected\n", "unexpected warning\n")
        with patch.object(signing, "invoke", return_value=result):
            self.assertEqual(signing.run(["git", "rev-parse", "HEAD"]), "expected\n")
            self.assertEqual(signing.run(["git", "verify-tag", "--raw", "tag"]),
                             "expected\nunexpected warning\n")


if __name__ == "__main__":
    unittest.main()
