"""Exercise credential-safe output and failure paths with real subprocesses."""

from contextlib import redirect_stderr, redirect_stdout
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

    def test_private_logs_append_with_private_permissions(self):
        with tempfile.TemporaryDirectory() as temp:
            logs = Path(temp) / "nested/logs"
            with patch.object(guard.sys, "stdout"), patch.object(guard.sys, "stderr"):
                guard.write_logs([b"first", b"error"], logs)
                guard.write_logs([b"second", b"more"], logs)
            self.assertEqual(logs.stat().st_mode & 0o777, 0o700)
            self.assertEqual((logs / "stdout.log").stat().st_mode & 0o777, 0o600)
            self.assertEqual((logs / "stdout.log").read_bytes(), b"firstsecond")
            self.assertEqual((logs / "stderr.log").read_bytes(), b"errormore")

    def test_unsafe_log_paths_never_redirect_writes_or_change_child_status(self):
        for kind in ("directory-symlink", "parent-symlink", "file-symlink", "hardlink",
                     "shared-directory", "shared-parent", "shared-file", "foreign-directory", "foreign-file"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                victim = base / "victim"
                victim.write_bytes(b"unchanged")
                victim.chmod(0o600)
                logs = base / "logs"
                logs.mkdir(mode=0o700)
                foreign_inode = None
                if kind == "directory-symlink":
                    logs.rmdir()
                    logs.symlink_to(base, target_is_directory=True)
                elif kind == "parent-symlink":
                    alias = base / "alias"
                    alias.symlink_to(logs, target_is_directory=True)
                    logs = alias / "nested"
                elif kind == "file-symlink":
                    (logs / "stdout.log").symlink_to(victim)
                elif kind == "hardlink":
                    os.link(victim, logs / "stdout.log")
                elif kind in ("shared-directory", "shared-parent"):
                    logs.chmod(0o777)
                    if kind == "shared-parent":
                        logs = logs / "nested"
                elif kind in ("shared-file", "foreign-file"):
                    output = logs / "stdout.log"
                    output.write_bytes(b"unchanged")
                    output.chmod(0o644 if kind == "shared-file" else 0o600)
                    if kind == "foreign-file":
                        foreign_inode = output.stat().st_ino
                elif kind == "foreign-directory":
                    foreign_inode = logs.stat().st_ino
                original_stat = os.fstat
                def observed_owner(descriptor):
                    info = original_stat(descriptor)
                    if info.st_ino == foreign_inode:
                        values = list(info)
                        values[4] = os.getuid() + 1
                        return os.stat_result(values)
                    return info
                with patch.object(guard.os, "fstat", side_effect=observed_owner), \
                        patch.object(guard.sys, "stdout"), patch.object(guard.sys, "stderr"), \
                        patch.object(guard.os, "write") as diagnostic:
                    with signing.session_signals() as interrupted:
                        status = guard.capture([sys.executable, "-c", "print('new'); raise SystemExit(11)"],
                                               os.environ, [], logs, interrupted)
                self.assertEqual(status, 11)
                self.assertEqual(victim.read_bytes(), b"unchanged")
                diagnostic.assert_called_with(2, b"Sanitized release output could not be fully written; values withheld.\n")
                if kind in ("shared-file", "foreign-file"):
                    self.assertEqual(output.read_bytes(), b"unchanged")

    def test_log_directory_descriptor_survives_path_replacement(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            logs, moved, victim = (base / name for name in ("logs", "moved", "victim"))
            logs.mkdir(mode=0o700)
            victim.mkdir(mode=0o700)
            original_open = os.open
            def replace_path(name, *args, **kwargs):
                if name == "stdout.log":
                    logs.rename(moved)
                    logs.symlink_to(victim, target_is_directory=True)
                return original_open(name, *args, **kwargs)
            with patch.object(guard.os, "open", side_effect=replace_path), \
                    patch.object(guard.sys, "stdout"), patch.object(guard.sys, "stderr"), \
                    patch.object(guard.os, "write"):
                guard.write_logs([b"retained", b"error"], logs)
            self.assertEqual((moved / "stdout.log").read_bytes(), b"retained")
            self.assertEqual(list(victim.iterdir()), [])

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
    def test_direct_guard_enforces_local_operator_policy_before_any_work(self):
        for command in (["check-user"], ["check-environment"], ["checkout-target"],
                        ["check-publication-target"],
                        ["check-target"], ["record-source"], ["check-source"],
                        ["run", "unused"], ["publish"]):
            for actor, triggering in (("other", ""), ("scylladb-publisher", "other"), ("", "")):
                with self.subTest(command=command, actor=actor, triggering=triggering), \
                        patch.dict(os.environ, {"GITHUB_ACTIONS": "false", "GITHUB_ACTOR": actor,
                                               "GITHUB_TRIGGERING_ACTOR": triggering}, clear=True), \
                        patch.object(sys, "argv", ["guard.py", *command]), \
                        patch.object(signing, "run", return_value="other-local-user") as run, \
                        patch.object(guard, "capture") as capture, redirect_stderr(StringIO()):
                    self.assertEqual(guard.main(), 2)
                    run.assert_called_once_with(["gh", "api", "user", "--jq", ".login"])
                    capture.assert_not_called()

    def test_publication_requires_workflow_token_without_legacy_fallback(self):
        for env in ({"GITHUB_ACTIONS": "true", "NUGET_API_KEY": "legacy-key"},
                    {"GITHUB_ACTIONS": "true", "NUGET_PUBLISH_TOKEN": ""},
                    {"GITHUB_ACTIONS": "false", "NUGET_PUBLISH_TOKEN": "temporary-token"},
                    {"GITHUB_ACTIONS": "true", "NUGET_PUBLISH_TOKEN": "temporary-token",
                     "NUGET_SOURCE": "https://other.example/index.json"}):
            with self.subTest(env=env), patch.dict(os.environ, {**env, "RELEASE_SOURCE_REF": "heads/main"}, clear=True), \
                    patch.object(sys, "argv", ["guard.py", "publish"]), \
                    patch.object(guard, "authorize_operator"), \
                    patch.object(guard, "verify_outputs", return_value=[Path("test.nupkg")]), \
                    patch.object(guard, "capture") as capture, redirect_stderr(StringIO()):
                self.assertEqual(guard.main(), 2)
                capture.assert_not_called()

    def test_non_main_publication_is_rejected_before_verification_or_credentials(self):
        for source in ("tags/2.0.5", "heads/release/2.0", ""):
            with self.subTest(source=source), \
                    patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RELEASE_SOURCE_REF": source}, clear=True), \
                    patch.object(sys, "argv", ["guard.py", "publish"]), \
                    patch.object(guard, "verify_outputs") as verify, \
                    patch.object(guard, "capture") as publish, redirect_stderr(StringIO()):
                self.assertEqual(guard.main(), 2)
                verify.assert_not_called()
                publish.assert_not_called()

    def test_early_publication_target_command_rejects_historical_sources(self):
        helper = Path(__file__).with_name("guard.py")
        for target, code in (("main", 0), ("2.0.5", 2), ("release/2.0", 2)):
            with self.subTest(target=target):
                env = {**os.environ, "GITHUB_ACTIONS": "true", "RELEASE_TARGET_TAG": target}
                result = subprocess.run([sys.executable, str(helper), "check-publication-target"],
                                        env=env, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, code)
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/release.yml").read_text()
        early = workflow.index("Reject non-main package publication before preparation")
        self.assertIn("if: inputs.dry-run == false", workflow[early:early + 240])
        self.assertLess(early, workflow.index("Checkout selected release target"))
        self.assertLess(early, workflow.index("Plan release versions and source tree"))
        self.assertLess(early, workflow.index("Authenticate NuGet trusted publisher"))

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

    def test_validation_precedes_protected_release_and_transfers_only_package_data(self):
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/release.yml").read_text()
        validation, release = workflow.split("  release:\n")
        self.assertIn("  validate:\n", validation)
        self.assertIn("    needs: validate\n", release)
        self.assertEqual(workflow.count("    runs-on: ubuntu-latest\n"), 2)
        self.assertNotIn("environment:", validation)
        self.assertIn("      actions: read\n", validation)
        for capability in ("secrets.", "id-token:", "NUGET_PUBLISH_TOKEN", "NuGet/login"):
            self.assertNotIn(capability, validation)
        self.assertIn("    environment: release\n", release)
        preflight = "run: python3 scripts/release/guard.py check-environment"
        self.assertIn(preflight, validation)
        self.assertLess(validation.index(preflight), validation.index("Checkout selected release target"))
        helpers = validation.split("      - name: Test trusted release helpers\n")[1].split("      - name:")[0]
        self.assertIn("run: make test-release-signing", helpers)
        self.assertNotIn("if:", helpers)
        self.assertLess(validation.index("Test trusted release helpers"),
                        validation.index("Checkout selected release target"))
        self.assertIn(preflight, release)
        self.assertLess(release.index(preflight), release.index("Sign and verify NuGet packages and checksums"))
        self.assertIn("          ref: ${{ github.sha }}\n", release)
        self.assertNotIn("inputs.target-tag", release)
        for executable in ("release-build", "release-prepare", "dotnet restore", "dotnet test",
                           "try-get", "checkout-one-commit-before"):
            self.assertNotIn(executable, release)
        upload = validation.split("      - name: Upload validated packages\n")[1].split("      - name:")[0]
        self.assertIn("          path: nupkgs/*.nupkg\n", upload)
        self.assertIn("          if-no-files-found: error\n", upload)
        self.assertIn("      package-artifact-id: ${{ steps.packages.outputs.artifact-id }}", validation)
        for output in ("source-sha", "source-ref", "source-ref-sha"):
            self.assertIn(output + ": ${{ steps.source.outputs." + output + " }}", validation)
            self.assertIn("${{ needs.validate.outputs." + output + " }}", release)
        self.assertLess(validation.index("Record selected release source"),
                        validation.index("Build and check release artifacts"))
        self.assertEqual(release.count('run: python3 "$RUNNER_TEMP/release-policy/scripts/release/guard.py" check-source'), 2)
        self.assertLess(release.index("Check validated source before signing"),
                        release.index("Sign and verify NuGet packages and checksums"))
        self.assertLess(release.index("Check validated source before publication"),
                        release.index("Authenticate NuGet trusted publisher"))
        signing_check = release.split("      - name: Check validated source before signing\n")[1].split("      - name:")[0]
        self.assertNotIn("if:", signing_check)
        self.assertNotIn("secrets.", signing_check)
        self.assertIn("artifact-ids: ${{ needs.validate.outputs.package-artifact-id }}", release)
        self.assertIn("          merge-multiple: true\n", release)
        download = release.split("      - name: Download validated packages as data\n")[1].split("      - name:")[0]
        # Without run-id/github-token overrides the action resolves IDs only
        # among this run's artifacts; the ID is the validation job's output.
        self.assertNotIn("run-id:", download)
        self.assertNotIn("github-token:", download)
        signing_step = release.split("      - name: Sign and verify NuGet packages and checksums\n")[1].split("      - name:")[0]
        self.assertIn('run: make -f "$RUNNER_TEMP/release-policy/Makefile" release-sign', signing_step)
        self.assertNotIn("if:", signing_step)
        dry_run = release.split("      - name: Perform release dry-run\n")[1].split("      - name:")[0]
        self.assertIn("if: inputs.dry-run == true", dry_run)
        self.assertIn('run: make -f "$RUNNER_TEMP/release-policy/Makefile" release-dry-run-summary', dry_run)
        self.assertIn("name: validated-packages-${{ github.run_attempt }}", upload)
        self.assertIn("name: nuget-package-${{ github.run_attempt }}", release)
        self.assertLess(validation.index("Preserve repository signing policy"),
                        validation.index("Checkout selected release target"))
        self.assertIn("      contents: write\n", release)
        self.assertIn('git -c core.hooksPath=/dev/null checkout --detach "$RELEASE_SOURCE_SHA"', release)
        self.assertLess(release.index("Preserve trusted release tools"),
                        release.index("Fetch validated source without executing project code"))
        self.assertLess(release.index("Verify prepared history with public key only"),
                        release.index("Authenticate NuGet trusted publisher"))
        push = release.split("      - name: Publish verified release history\n")[1].split("      - name:")[0]
        self.assertIn("if: inputs.dry-run == false", push)
        self.assertLess(release.index("      - name: Perform release\n"),
                        release.index("Publish verified release history"))

    def test_unsafe_inputs_are_rejected_without_echoing_values(self):
        for name, value in [("RELEASE_VERSION", '1.0.0$(touch unsafe)'),
                            ("PACKAGE_OUTPUT_DIR", 'nupkgs; touch unsafe'),
                            ("NUGET_SOURCE", 'https://user:secret@example.invalid/index.json'),
                            ("DOTNET_VERBOSITY", 'diagnostic'), ("MSBUILDDEBUGENGINE", '1')]:
            with self.subTest(name=name), patch.dict(os.environ, {
                    name: value, "GITHUB_ACTIONS": "true", "GITHUB_ACTOR": "scylladb-publisher"}, clear=True):
                output = StringIO()
                with patch.object(sys, "argv", ["guard.py", "run", "unused"]), \
                        patch.object(guard, "capture") as capture, redirect_stderr(output):
                    self.assertEqual(guard.main(), 2)
                    capture.assert_not_called()
                self.assertNotIn(value, output.getvalue())

    def test_debug_and_credential_options_are_rejected(self):
        for args in (["--verbosity", "diagnostic"], ["-v:diag"],
                     ["-v:d"], ["-v", "d"], ["--verbosity=d"], ["-verbosity:d"], ["/v:D"],
                     ["-flp:logfile=raw.log"], ["-fileLoggerParameters:LogFile=raw.log"],
                     ["/distributedFileLogger"], ["/flp1:LogFile=raw.log"],
                     ["-dfl"], ["/dfl"],
                     ["/bl:build.binlog"],
                     ["/fl"], ["/p:Password=private"], ["--api-key", "private"]):
            with self.subTest(args=args), self.assertRaises(guard.Unsafe):
                guard.check_command(["dotnet", "build", *args])
        guard.check_command(["dotnet", "restore", "--verbosity", "minimal"])
        guard.check_command(["dotnet", "build", "-v:normal"])

    def test_grouped_properties_reject_credential_keys_without_rejecting_nonsecret_values(self):
        for argument in ("-p:Configuration=Release;Password=private",
                         "/property:Configuration=Release,ApiKey=private",
                         "-rp:Configuration=Release;RestoreToken=private",
                         "/restoreProperty:Configuration=Release,Password=private",
                         '-p:Configuration=Release;"Password"=private',
                         '-p:Configuration=Release;Pass"word"=private',
                         '-p:Pass""word=private',
                         '-p:Configuration=Release;"ApiKey=private"',
                         '-p:Description="Public;Password=label";ApiKey=private',
                         r'-p:Description="Public\\";Password=private',
                         r'-p:Description="Public\"";Password=private',
                         '-p:Description=Pub""lic;Password=private'):
            with self.subTest(argument=argument), self.assertRaises(guard.Unsafe):
                guard.check_command(["dotnet", "build", argument])
        guard.check_command(["dotnet", "build", "-p:Configuration=Release;Version=1.2.3",
                             "/property:Description=password token secret,PackageVersion=1.2.3",
                             "-rp:Configuration=Release;RestoreSources=https://example.invalid",
                             '-p:Description="Public;Password=label"',
                             '-p:Description="Public,ApiKey=label"',
                             '-p:Description=Pub""lic;Version=1.2.3',
                             '-p:Description="Pub""lic"',
                             r'-p:Description="Public\";Password=label"'])

    def test_protected_environment_fails_closed_on_weaker_settings(self):
        environment = {"can_admins_bypass": False,
                       "protection_rules": [{"type": "required_reviewers", "prevent_self_review": True,
                                             "reviewers": [{"type": "Team", "reviewer": {
                                                 "slug": "drivers", "html_url": "https://github.com/orgs/scylladb/teams/drivers"}}]}],
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
            for reviewers in ([{"type": "Team", "reviewer": {"slug": "other"}}],
                              [{"type": "User", "reviewer": {"login": "drivers"}}],
                              [{"type": "Team", "reviewer": {"slug": "drivers",
                                "html_url": "https://github.com/orgs/other/teams/drivers"}}],
                              environment["protection_rules"][0]["reviewers"] * 2,
                              environment["protection_rules"][0]["reviewers"] + [{"type": "User"}]):
                candidate = copy.deepcopy(environment)
                candidate["protection_rules"][0]["reviewers"] = reviewers
                invalid.append(candidate)
            for candidate in invalid:
                with self.subTest(environment=candidate), \
                        patch.object(signing, "run", side_effect=[json.dumps(candidate), json.dumps(branches)]), \
                        self.assertRaises(guard.Unsafe):
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


class SourceTests(unittest.TestCase):
    def setUp(self):
        fixture = tempfile.TemporaryDirectory(prefix="release-source-test-")
        self.addCleanup(fixture.cleanup)
        original = Path.cwd()
        self.addCleanup(os.chdir, original)
        self.directory = Path(fixture.name)
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(("GIT_", "GITHUB_", "RELEASE_", "MAKE"))}
        env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull,
                   GITHUB_REPOSITORY="owner/repo", RELEASE_TARGET_TAG="main")
        self.enterContext(patch.dict(os.environ, env, clear=True))
        os.chdir(self.directory)
        self.remote = self.directory / "remote.git"
        signing.run(["git", "init", "--bare", "--quiet", str(self.remote)])
        signing.run(["git", "init", "--quiet", "--initial-branch=main", "source"])
        os.chdir(self.directory / "source")
        signing.run(["git", "remote", "add", "origin", str(self.remote)])
        self.commit()
        signing.run(["git", "push", "--quiet", "origin", "main"])
        os.environ["GITHUB_SHA"] = signing.run(["git", "rev-parse", "HEAD"]).strip()

    def commit(self):
        signing.run(["git", "-c", "user.name=Release Test", "-c", "user.email=release@example.invalid",
                     "-c", "commit.gpgsign=false", "commit", "--quiet", "--allow-empty", "-m", "Source"])

    def record(self):
        output = StringIO()
        with redirect_stdout(output):
            guard.record_source()
        values = dict(line.split("=", 1) for line in output.getvalue().splitlines())
        os.environ.update({"RELEASE_" + key.upper().replace("-", "_"): value
                           for key, value in values.items()})
        return values

    def check(self):
        # Substitute only GitHub's ref API; read its response from a real bare remote.
        run = signing.run
        def remote_ref(command, **kwargs):
            if command[0] != "gh":
                return run(command, **kwargs)
            prefix = "repos/owner/repo/git/ref/"
            self.assertEqual(command[:2], ["gh", "api"])
            self.assertTrue(command[2].startswith(prefix))
            ref = "refs/" + urllib.parse.unquote(command[2][len(prefix):])
            sha = run(["git", "--git-dir", str(self.remote), "rev-parse", "--verify", ref]).strip()
            return json.dumps({"ref": ref, "object": {"sha": sha}})
        with patch.object(signing, "run", side_effect=remote_ref):
            guard.check_source()

    def test_branch_advance_after_validation_is_rejected(self):
        values = self.record()
        self.assertEqual(values["source-sha"], os.environ["GITHUB_SHA"])
        self.assertEqual(values["source-ref"], "heads/main")
        self.check()
        self.commit()
        signing.run(["git", "push", "--quiet", "origin", "main"])
        with self.assertRaises(guard.Unsafe):
            self.check()

    def test_main_advance_since_dispatch_is_rejected_before_build(self):
        self.commit()
        signing.run(["git", "push", "--quiet", "origin", "main"])
        with redirect_stdout(StringIO()), self.assertRaises(guard.Unsafe):
            guard.record_source()

    def test_main_tag_cannot_replace_the_main_branch(self):
        signing.run(["git", "-c", "tag.gpgSign=false", "tag", "main"])
        self.assertEqual(self.record()["source-ref"], "heads/main")
        self.check()

    def test_deleted_source_ref_is_rejected(self):
        self.record()
        signing.run(["git", "--git-dir", str(self.remote), "update-ref", "-d", "refs/heads/main"])
        with self.assertRaises(signing.SigningError):
            self.check()

    def test_historical_tag_records_parent_and_rejects_moved_tag(self):
        parent = os.environ["GITHUB_SHA"]
        self.commit()
        signing.run(["git", "-c", "user.name=Release Test", "-c", "user.email=release@example.invalid",
                     "-c", "tag.gpgSign=false", "tag", "-a", "-m", "Release", "v1.0.0"])
        signing.run(["git", "push", "--quiet", "origin", "refs/tags/v1.0.0"])
        signing.run(["git", "checkout", "--quiet", "--detach", "v1.0.0~1"])
        os.environ["RELEASE_TARGET_TAG"] = "v1.0.0"
        values = self.record()
        self.assertEqual(values["source-sha"], parent)
        self.assertEqual(values["source-ref"], "tags/v1.0.0")
        self.assertNotEqual(values["source-ref-sha"], parent)
        self.check()
        signing.run(["git", "-c", "tag.gpgSign=false", "tag", "-f", "v1.0.0", parent])
        signing.run(["git", "push", "--quiet", "--force", "origin", "refs/tags/v1.0.0"])
        with self.assertRaises(guard.Unsafe):
            self.check()
        self.assertEqual(signing.run(["git", "rev-parse", "HEAD"]).strip(), parent)

    def test_branch_with_slash_is_supported(self):
        signing.run(["git", "checkout", "--quiet", "-b", "release/1.0"])
        signing.run(["git", "push", "--quiet", "origin", "release/1.0"])
        os.environ["RELEASE_TARGET_TAG"] = "release/1.0"
        self.assertEqual(self.record()["source-ref"], "heads/release/1.0")
        self.check()

    def test_missing_or_inconsistent_source_metadata_fails_closed(self):
        self.record()
        for name, value in (("RELEASE_SOURCE_SHA", ""), ("RELEASE_SOURCE_SHA", "invalid"),
                            ("RELEASE_SOURCE_SHA", "0" * 40), ("RELEASE_SOURCE_REF_SHA", ""),
                            ("RELEASE_SOURCE_REF", "heads/main\n"),
                            ("RELEASE_SOURCE_REF", "pull/86/head"), ("GITHUB_REPOSITORY", "")):
            with self.subTest(name=name, value=value), patch.dict(os.environ, {name: value}), \
                    self.assertRaises((guard.Unsafe, signing.SigningError)):
                self.check()


if __name__ == "__main__":
    unittest.main()
