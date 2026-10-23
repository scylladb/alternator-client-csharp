"""Exercise release signing with disposable protected keys and real Git objects."""

from contextlib import contextmanager, redirect_stderr
from io import StringIO
import json
import os
from pathlib import Path
import shutil
import signal
import time
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import guard
import signing

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY_PRIMARY = signing.PRIMARY
REPOSITORY_SIGNING = signing.SIGNING


@unittest.skipUnless(shutil.which("gpg") and shutil.which("git"), "GnuPG and Git required")
class SigningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = tempfile.TemporaryDirectory(prefix="repo-signing-fixture-")
        cls.directory = Path(cls.fixture.name)
        cls.keyring = cls.directory / "gnupg"
        cls.keyring.mkdir(mode=0o700)
        cls.password = "test-only-release-passphrase"
        def gpg(*args):
            result = subprocess.run(
                ["gpg", "--no-options", "--homedir", str(cls.keyring), "--batch",
                 "--pinentry-mode", "loopback", "--passphrase-fd", "0", *args],
                input=cls.password + "\n", capture_output=True, text=True, check=True, timeout=60,
            )
            return result.stdout
        gpg("--quick-generate-key", "Release Test <release@example.invalid>", "rsa2048", "cert", "1d")
        cls.primary = [line.split(":")[9] for line in
                       gpg("--with-colons", "--list-keys").splitlines() if line.startswith("fpr:")][0]
        gpg("--quick-add-key", cls.primary, "rsa2048", "sign", "1d")
        cls.subkey = [line.split(":")[9] for line in
                      gpg("--with-colons", "--list-keys").splitlines() if line.startswith("fpr:")][1]
        cls.public = cls.directory / "public.asc"
        cls.public.write_text(gpg("--armor", "--export", cls.primary))
        cls.private = cls.directory / "subkey.asc"
        cls.private.write_text(gpg("--armor", "--export-secret-subkeys", cls.primary))
        cls.full = cls.directory / "full.asc"
        cls.full.write_text(gpg("--armor", "--export-secret-keys", cls.primary))

    @classmethod
    def tearDownClass(cls):
        subprocess.run(["gpgconf", "--homedir", str(cls.keyring), "--kill", "gpg-agent"],
                       capture_output=True, check=True, timeout=60)
        cls.fixture.cleanup()

    @contextmanager
    def repository(self, full=False, password=None):
        original = Path.cwd()
        with tempfile.TemporaryDirectory(prefix="repo-signing-test-") as temp:
            directory = Path(temp)
            env = {key: value for key, value in os.environ.items()
                   if not key.startswith(("GIT_", "GITHUB_", "RELEASE_", "MAKE"))
                   and key not in ("PACKAGE_OUTPUT_DIR", "GOBIN")}
            env.update(RUNNER_TEMP=str(directory), GITHUB_ENV=str(directory / "github-env"),
                       GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(directory / "gitconfig"),
                       RELEASE_GPG_PRIVATE_KEY_FILE=str(self.full if full else self.private),
                       RELEASE_GPG_PASSPHRASE=self.password if password is None else password)
            Path(env["GIT_CONFIG_GLOBAL"]).write_text("")
            with patch.dict(os.environ, env, clear=True), \
                    patch.multiple(signing, PRIMARY=self.primary, SIGNING=self.subkey,
                                   PUBLIC_KEY=self.public):
                repo = directory / "source"
                signing.run(["git", "init", "--quiet", str(repo)])
                os.chdir(repo)
                try:
                    signing.run(["git", "-c", "user.name=Initial", "-c",
                                 "user.email=initial@example.invalid", "-c", "commit.gpgsign=false",
                                 "commit", "--quiet", "--allow-empty", "-m", "Initial"])
                    yield directory
                finally:
                    os.chdir(original)

    @contextmanager
    def session(self):
        with signing.signing_session() as env, patch.dict(os.environ, env, clear=True):
            self.assertNotIn("RELEASE_GPG_PRIVATE_KEY", env)
            self.assertNotIn("RELEASE_GPG_PRIVATE_KEY_FILE", env)
            yield env

    def release(self):
        signing.run(["git", "commit", "--quiet", "--allow-empty", "-m", "Release"])
        signing.run(["git", "tag", "-s", "-m", "Release 1.0", "v1.0.0"])

    def test_repository_setup_failure_restores_working_directory(self):
        original = Path.cwd()
        real_run = signing.run
        def fail_commit(command, env=None):
            if "commit" in command:
                raise signing.SigningError("fixture commit failed")
            return real_run(command, env)
        with patch.object(signing, "run", side_effect=fail_commit):
            with self.assertRaisesRegex(signing.SigningError, "fixture commit failed"):
                with self.repository():
                    self.fail("Initial commit failure accepted")
        self.assertEqual(Path.cwd(), original)
        with self.repository():
            self.assertTrue(Path(".git").is_dir())

    def test_signed_commit_and_tag_verify_after_session(self):
        with self.repository() as directory:
            # Existing checkout settings must not override the session.
            signing.run(["git", "config", "user.signingkey", "invalid-key"])
            config = Path(".git/config").read_bytes()
            with self.session():
                self.release()
            signing.verify_commit("HEAD")
            signing.verify_tag("v1.0.0", signing.run(["git", "rev-parse", "HEAD"]).strip())
            self.assertEqual(Path(".git/config").read_bytes(), config)
            self.assertFalse(list(directory.glob("release-signing-*")))
            self.assertFalse(Path(os.environ["GITHUB_ENV"]).exists())

    def test_empty_passphrase_fails_before_configuration(self):
        with self.repository(password="") as directory:
            with self.assertRaisesRegex(signing.SigningError, "nonempty"), self.session():
                self.fail("Empty passphrase accepted")
            self.assertFalse(list(directory.glob("release-signing-*")))

    def test_wrong_passphrase_fails_with_fresh_agent(self):
        with self.repository(password="wrong-test-passphrase") as directory:
            with self.assertRaises(signing.SigningError), self.session():
                self.fail("Wrong passphrase accepted")
            self.assertFalse(list(directory.glob("release-signing-*")))

    def test_primary_secret_rejected(self):
        with self.repository(full=True):
            with self.assertRaisesRegex(signing.SigningError, "signing-private.asc"), self.session():
                self.fail("Primary private key accepted")

    def test_unsigned_commit_and_tag_rejected(self):
        with self.repository():
            with self.session():
                signing.run(["git", "-c", "commit.gpgsign=false", "commit", "--quiet",
                             "--allow-empty", "-m", "Unsigned"])
                signing.run(["git", "-c", "tag.gpgSign=false", "tag", "-a", "-m",
                             "Unsigned", "v1.0.0"])
            with self.assertRaises(signing.SigningError):
                signing.verify_commit("HEAD")
            with self.assertRaises(signing.SigningError):
                signing.verify_tag("v1.0.0")

    def test_tag_at_unexpected_commit_rejected(self):
        with self.repository():
            with self.session():
                self.release()
                signing.run(["git", "commit", "--quiet", "--allow-empty", "-m", "Later"])
            with self.assertRaisesRegex(signing.SigningError, "unexpected commit"):
                signing.verify_tag("v1.0.0", signing.run(["git", "rev-parse", "HEAD"]).strip())

    def test_batch_signing_reuse_and_tamper_rejection(self):
        with self.repository(), self.session():
            files = [Path("package.nupkg"), Path("SHA256SUMS")]
            for artifact in files:
                artifact.write_bytes(b"release package")
            signing.sign_files(files)
            signatures = [Path(str(artifact) + ".asc").read_bytes() for artifact in files]
            signing.sign_files(files)
            self.assertEqual([Path(str(artifact) + ".asc").read_bytes() for artifact in files], signatures)
            files[0].write_bytes(b"changed package")
            with self.assertRaises(signing.SigningError):
                signing.sign_files(files)

    def test_exec_cleans_up_after_command_failure(self):
        with self.repository() as directory:
            config = Path(".git/config").read_bytes()
            with patch("sys.argv", ["signing.py", "exec", sys.executable, "-c", "raise SystemExit(7)"]):
                self.assertEqual(signing.main(), 7)
            self.assertEqual(Path(".git/config").read_bytes(), config)
            self.assertFalse(list(directory.glob("release-signing-*")))

    def test_exec_accepts_passphrase_file(self):
        with self.repository() as directory:
            password = directory / "passphrase"
            password.write_text(self.password + "\n")
            os.environ["RELEASE_GPG_PASSPHRASE_FILE"] = str(password)
            del os.environ["RELEASE_GPG_PASSPHRASE"]
            with patch("sys.argv", ["signing.py", "exec", "git", "commit", "--quiet",
                                    "--allow-empty", "-m", "Signed from file credentials"]):
                self.assertEqual(signing.main(), 0)
            signing.verify_commit("HEAD")
            self.assertFalse(list(directory.glob("release-signing-*")))

    def test_cleanup_removes_files_even_when_agent_shutdown_fails(self):
        with self.repository() as directory:
            real_run = signing.run
            def fail_shutdown(command, env=None):
                real_run(command, env)
                raise signing.SigningError("shutdown failed")
            with patch.object(signing, "run", wraps=real_run) as command:
                with self.session():
                    command.side_effect = fail_shutdown
            self.assertFalse(list(directory.glob("release-signing-*")))

    def test_first_git_signature_requires_current_passphrase_after_preflight(self):
        with self.repository(), self.session():
            os.environ["RELEASE_GPG_PASSPHRASE"] = "incorrect-fixture-passphrase"
            with self.assertRaises(signing.SigningError):
                signing.run(["git", "commit", "--allow-empty", "-qm", "Must fail"])
            os.environ["RELEASE_GPG_PASSPHRASE"] = self.password
            signing.run(["git", "commit", "--allow-empty", "-qm", "Correct passphrase"])
            signing.verify_commit("HEAD")

    def test_unprotected_native_export_is_rejected(self):
        with self.repository() as directory:
            home = directory / "unprotected"
            home.mkdir(mode=0o700)
            command = ["gpg", "--no-options", "--homedir", str(home), "--batch",
                       "--pinentry-mode", "loopback", "--passphrase-fd", "0"]
            def gpg(*args):
                return subprocess.run([*command, *args], input="\n", text=True, capture_output=True,
                                      check=True, timeout=60).stdout
            try:
                gpg("--quick-generate-key", "Unprotected Test <test@example.invalid>", "ed25519", "cert", "1d")
                primary = next(line.split(":")[9] for line in gpg("--with-colons", "--list-keys").splitlines()
                               if line.startswith("fpr:"))
                gpg("--quick-add-key", primary, "ed25519", "sign", "1d")
                subkey = [line.split(":")[9] for line in gpg("--with-colons", "--list-keys").splitlines()
                          if line.startswith("fpr:")][1]
                private = directory / "unprotected.asc"
                private.write_text(gpg("--armor", "--export-secret-subkeys", primary))
                public = directory / "unprotected-public.asc"
                public.write_text(gpg("--armor", "--export", primary))
                with patch.dict(os.environ, {"RELEASE_GPG_PRIVATE_KEY_FILE": str(private)}), \
                        patch.multiple(signing, PRIMARY=primary, SIGNING=subkey, PUBLIC_KEY=public):
                    with self.assertRaises(signing.SigningError), self.session():
                        self.fail("Unprotected key accepted")
                self.assertFalse(list(directory.glob("release-signing-*")))
            finally:
                subprocess.run(["gpgconf", "--homedir", str(home), "--kill", "gpg-agent"],
                               capture_output=True, check=True, timeout=60)

    def test_historical_checkout_preserves_tools_and_existing_tag(self):
        with self.make_repository(preserved=True) as directory:
            baseline = signing.run(["git", "rev-parse", "HEAD"]).strip()
            with self.session():
                self.release()
            tag = signing.run(["git", "rev-parse", "refs/tags/v1.0.0"]).strip()
            signing.run(["make", "-f", str(directory / "release-policy/Makefile"),
                         "checkout-one-commit-before", "RELEASE_TARGET_TAG=v1.0.0"])
            self.assertEqual(signing.run(["git", "rev-parse", "HEAD"]).strip(), baseline)
            self.assertEqual(signing.run(["git", "rev-parse", "refs/tags/v1.0.0"]).strip(), tag)
            self.assertTrue((directory / "release-policy/scripts/release/guard.py").is_file())

    def test_post_preflight_subkey_protection_is_rechecked(self):
        with self.repository() as directory:
            real_check = signing.protected_subkey
            checks = []
            def materialized(env):
                checks.append(True)
                real_check(env)
                if len(checks) == 2:
                    raise signing.SigningError("unprotected materialized subkey")
            with patch.object(signing, "protected_subkey", side_effect=materialized):
                with self.assertRaisesRegex(signing.SigningError, "materialized"), self.session():
                    self.fail("Unprotected materialized key accepted")
            self.assertEqual(len(checks), 2)
            self.assertFalse(list(directory.glob("release-signing-*")))

    def test_cleanup_failure_preserves_command_status(self):
        for status in (0, 7):
            with self.subTest(status=status), self.repository() as directory:
                real_execute = signing.execute
                real_run = signing.run
                def shutdown_failure(command, **kwargs):
                    real_run(command, **kwargs)
                    raise signing.SigningError("cleanup failed")
                def execute(command, env):
                    result = real_execute(command, env)
                    mocked.side_effect = shutdown_failure
                    return result
                with patch.object(signing, "run", wraps=real_run) as mocked, \
                        patch.object(signing, "execute", side_effect=execute), \
                        patch("sys.argv", ["signing.py", "exec", sys.executable, "-c", "raise SystemExit(" + str(status) + ")"]):
                    self.assertEqual(signing.main(), status)
                self.assertFalse(list(directory.glob("release-signing-*")))

    def test_cancellation_stops_signing_children_and_removes_keyring(self):
        with self.make_repository() as directory:
            marker = directory / "child-pid"
            code = ("import os, subprocess, sys, time; "
                    "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)']); "
                    "open(sys.argv[1], 'w').write(str(child.pid)); time.sleep(60)")
            helper = Path("scripts/release/signing.py").resolve()
            process = subprocess.Popen([sys.executable, str(helper), "exec", sys.executable,
                                        "-c", code, str(marker)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                deadline = time.monotonic() + 15
                while not marker.exists() and process.poll() is None and time.monotonic() < deadline:
                    time.sleep(.05)
                self.assertTrue(marker.exists())
                process.send_signal(signal.SIGTERM)
                process.communicate(timeout=15)
                self.assertEqual(process.returncode, 128 + signal.SIGTERM)
                pid = int(marker.read_text())
                stat = Path('/proc') / str(pid) / 'stat'
                self.assertTrue(not stat.exists() or stat.read_text().split()[2] == 'Z')
                self.assertFalse(list(directory.glob("release-signing-*")))
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate()

    def test_direct_publication_rejects_unsigned_tampered_and_unlisted_packages(self):
        for corruption in ("unsigned", "tampered", "unlisted"):
            with self.subTest(corruption=corruption), self.make_repository():
                signing.run(["make", "release-prepare"])
                packages = list(Path("nupkgs").glob("*.nupkg"))
                if corruption == "unsigned":
                    Path(str(packages[0]) + ".asc").unlink()
                elif corruption == "tampered":
                    packages[0].write_bytes(b"tampered")
                else:
                    extra = Path("nupkgs/extra.1.0.0.nupkg")
                    extra.write_bytes(b"extra")
                    with self.session():
                        signing.sign_files([extra])
                result = subprocess.run(["make", "release-push", "NUGET_PUBLISH_TOKEN=test-only-publishing-key"],
                                        capture_output=True, text=True, timeout=60)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("test-only-publishing-key", result.stdout + result.stderr)
                commands = Path(os.environ["BUILD_OBSERVATIONS"]).read_text()
                self.assertNotIn('"push"', commands)

    @contextmanager
    def make_repository(self, preserved=False):
        with self.repository() as directory:
            policy = directory / "release-policy" if preserved else Path.cwd()
            policy.mkdir(exist_ok=True)
            shutil.copyfile(ROOT / "Makefile", policy / "Makefile")
            helper = policy / "scripts/release/signing.py"
            helper.parent.mkdir(parents=True, exist_ok=True)
            helper.write_text((ROOT / "scripts/release/signing.py").read_text()
                              .replace(REPOSITORY_PRIMARY, self.primary)
                              .replace(REPOSITORY_SIGNING, self.subkey))
            shutil.copyfile(self.public, helper.with_name("publisher.asc"))
            shutil.copyfile(ROOT / "scripts/release/guard.py", helper.with_name("guard.py"))
            if preserved:
                # Historical targets may contain neither release policy nor helpers.
                Path("Makefile").write_text("release-build release-sign:\n\t@exit 99\n")
            tools = directory / "bin"
            tools.mkdir()
            dotnet = tools / "dotnet"
            dotnet.write_text("#!/usr/bin/env python3\n" + "\n".join([
                "import json, os, pathlib, sys",
                "if sys.argv[1:3] == ['nuget', 'push']:",
                "    assert sys.argv[sys.argv.index('--api-key') + 1] == 'test-only-publishing-key'",
                "else:",
                "    assert not any(k.startswith('RELEASE_GPG_') for k in os.environ)",
                "    assert 'NUGET_API_KEY' not in os.environ",
                "    assert 'NUGET_PUBLISH_TOKEN' not in os.environ",
                "    assert 'GNUPGHOME' not in os.environ",
                "    assert not any(k.startswith(('GIT_CONFIG_KEY_', 'GIT_CONFIG_VALUE_')) for k in os.environ)",
                "    assert 'GIT_CONFIG_COUNT' not in os.environ",
                "assert not list(pathlib.Path(os.environ['RUNNER_TEMP']).glob('release-signing-*'))",
                "with open(os.environ['BUILD_OBSERVATIONS'], 'a') as log: log.write(json.dumps(sys.argv[1:]) + '\\n')",
                "if os.environ.get('FAIL_BUILD'): sys.exit(23)",
                "if sys.argv[1] == 'pack':",
                "    output = pathlib.Path(sys.argv[sys.argv.index('--output') + 1])",
                "    output.mkdir(exist_ok=True)",
                "    version = os.environ.get('RELEASE_VERSION') or '2.0.5'",
                "    (output / ('ScyllaDB.Alternator.' + version + '.nupkg')).write_bytes(b'release package')",
            ]) + "\n")
            dotnet.chmod(0o700)
            os.environ.update(PATH=str(tools) + os.pathsep + os.environ["PATH"],
                              GITHUB_ACTIONS="true", GITHUB_ACTOR="scylladb-publisher", GITHUB_TRIGGERING_ACTOR="scylladb-publisher",
                              BUILD_OBSERVATIONS=str(directory / "build-observations"),
                              MAKEFILE_PATH=str(Path.cwd()))
            password = directory / "passphrase"
            password.write_text(self.password + "\n")
            os.environ.update(RELEASE_GPG_PRIVATE_KEY=self.private.read_text(),
                              RELEASE_GPG_PASSPHRASE_FILE=str(password))
            yield directory

    def test_make_release_prepares_signed_packages_without_changing_git(self):
        with self.make_repository() as directory:
            before = signing.run(["git", "rev-parse", "HEAD"])
            config = Path(".git/config").read_bytes()
            signing.run(["make", "-n", "release-build"])
            observations = Path(os.environ["BUILD_OBSERVATIONS"])
            self.assertFalse(observations.exists())
            signing.run(["make", "-j2", "release-dry-run", "RELEASE_VERSION=1.0.0",
                         "PACKAGE_OUTPUT_DIR=nupkgs",
                         "RELEASE_GPG_PASSPHRASE=" + self.password])
            commands = observations.read_text()
            for command in ['"build"', '"format"', '"test"', '"pack"', '"add"', '"run"']:
                self.assertIn(command, commands)
            self.assertIn("/p:Version=1.0.0", commands)
            self.assertIn("/p:PackageVersion=1.0.0", commands)
            self.assertNotIn('"push"', commands)
            self.assertEqual(len(list(Path("nupkgs").glob("*.asc"))), 2)
            subprocess.run(["sha256sum", "--check", "SHA256SUMS"], cwd="nupkgs", check=True,
                           capture_output=True, timeout=60)
            with signing.public_environment() as env:
                for artifact in [Path("nupkgs/ScyllaDB.Alternator.1.0.0.nupkg"), Path("nupkgs/SHA256SUMS")]:
                    signing.verify_status(signing.gpg("--status-fd", "1", "--verify",
                                                     str(artifact) + ".asc", artifact, env=env))
            self.assertEqual(signing.run(["git", "rev-parse", "HEAD"]), before)
            self.assertEqual(Path(".git/config").read_bytes(), config)
            self.assertEqual(signing.run(["git", "tag", "--list"]), "")
            self.assertFalse(list(directory.glob("release-signing-*")))
            self.assertFalse(Path(os.environ["GITHUB_ENV"]).exists())

    def test_preserved_policy_builds_and_signs_historical_target(self):
        with self.make_repository(preserved=True) as directory:
            signing.run(["make", "-f", str(directory / "release-policy/Makefile"), "release-prepare",
                         "RELEASE_SKIP_TESTS=true", "RELEASE_VERSION=1.0.0"])
            commands = Path(os.environ["BUILD_OBSERVATIONS"]).read_text()
            self.assertNotIn('"build"', commands)
            self.assertNotIn('"format"', commands)
            self.assertNotIn('"test"', commands)
            for command in ['"pack"', '"add"', '"run"']:
                self.assertIn(command, commands)
            self.assertTrue(Path("nupkgs/ScyllaDB.Alternator.1.0.0.nupkg.asc").is_file())
            self.assertFalse((directory / "release-policy/nupkgs").exists())
            self.assertFalse(list(directory.glob("release-signing-*")))

    def test_checksum_order_matches_verification_in_non_c_locale(self):
        locales = signing.run(["locale", "-a"]).splitlines()
        selected = next((name for name in locales if name.lower().startswith("en_us.")), None)
        if selected is None:
            self.skipTest("an en_US locale is needed to exercise non-C filename collation")
        with self.make_repository(), patch.dict(os.environ, {"LC_ALL": selected, "PACKAGE_OUTPUT_DIR": "nupkgs"}):
            Path("nupkgs").mkdir()
            names = ["a.nupkg", "Z.nupkg", "_.nupkg", "+.nupkg", "-.nupkg", "A.nupkg"]
            for name in names:
                Path("nupkgs", name).write_bytes(name.encode())
            signing.run(["make", "release-sign"])
            entries = [line.split("  ", 1)[1] for line in Path("nupkgs/SHA256SUMS").read_text().splitlines()]
            self.assertEqual(entries, sorted(names))
            self.assertEqual([package.name for package in guard.verify_outputs()], sorted(names))

    def test_separate_release_workspace_signs_validated_bytes_with_trusted_tools(self):
        with self.make_repository(preserved=True) as validation:
            policy = validation / "release-policy"
            signing.run(["make", "-f", str(policy / "Makefile"), "release-build",
                         "RELEASE_SKIP_TESTS=true", "RELEASE_VERSION=1.0.0"])
            package = Path("nupkgs/ScyllaDB.Alternator.1.0.0.nupkg")
            validated_bytes = package.read_bytes()
            observations = Path(os.environ["BUILD_OBSERVATIONS"])
            built = observations.read_bytes()
            source = Path.cwd()
            with tempfile.TemporaryDirectory(prefix="release-runner-") as temp:
                release = Path(temp)
                # Only package data crosses from the selected target. Tools come
                # from the retained trusted policy, as with the pinned checkout.
                shutil.copyfile(policy / "Makefile", release / "Makefile")
                shutil.copytree(policy / "scripts", release / "scripts")
                (release / "nupkgs").mkdir()
                transferred = release / "nupkgs" / package.name
                shutil.copyfile(package, transferred)
                self.assertFalse((release / "ScyllaDB.Alternator.csproj").exists())
                try:
                    os.chdir(release)
                    with patch.dict(os.environ, {"MAKEFILE_PATH": str(release),
                                                 "PACKAGE_OUTPUT_DIR": str(release / "nupkgs"),
                                                 "RUNNER_TEMP": str(release)}):
                        signing.run(["make", "release-sign"])
                        signing.run(["make", "release-dry-run-summary"])
                        with signing.public_environment() as env:
                            for artifact in (transferred, release / "nupkgs/SHA256SUMS"):
                                signing.verify_status(signing.gpg("--status-fd", "1", "--verify",
                                                                 str(artifact) + ".asc", artifact, env=env))
                    self.assertEqual(transferred.read_bytes(), validated_bytes)
                    self.assertEqual(observations.read_bytes(), built)
                    self.assertFalse(list(release.glob("release-signing-*")))
                finally:
                    os.chdir(source)

    def test_release_rerun_uses_fresh_outputs_and_keeps_publishing_key_out_of_build(self):
        with self.make_repository():
            signing.run(["make", "release-prepare", "RELEASE_VERSION=1.0.0"])
            Path("nupkgs/notes.txt").write_text("unrelated retained file")
            signing.run(["make", "-n", "release-build"])
            self.assertTrue(Path("nupkgs/ScyllaDB.Alternator.1.0.0.nupkg.asc").is_file())
            signing.run(["make", "-j2", "release", "RELEASE_VERSION=2.0.0",
                         "NUGET_PUBLISH_TOKEN=test-only-publishing-key"])
            packages = list(Path("nupkgs").glob("*.nupkg"))
            self.assertEqual([path.name for path in packages], ["ScyllaDB.Alternator.2.0.0.nupkg"])
            self.assertEqual(len(list(Path("nupkgs").glob("*.asc"))), 2)
            self.assertEqual(Path("nupkgs/notes.txt").read_text(), "unrelated retained file")
            signing.sign_files([*packages, Path("nupkgs/SHA256SUMS")])
            subprocess.run(["sha256sum", "--check", "SHA256SUMS"], cwd="nupkgs", check=True,
                           capture_output=True, timeout=60)
            commands = [json.loads(line) for line in Path(os.environ["BUILD_OBSERVATIONS"]).read_text().splitlines()]
            pushes = [command for command in commands if command[:2] == ["nuget", "push"]]
            self.assertEqual(len(pushes), 1)
            self.assertEqual(Path(pushes[0][2]).name, packages[0].name)

    def test_release_build_removes_initial_makeflags_overrides(self):
        with self.make_repository():
            overrides = "-- RELEASE_GPG_PASSPHRASE=" + self.password
            overrides += " RELEASE_GPG_PRIVATE_KEY_FILE=" + str(self.private)
            overrides += " RELEASE_GPG_PASSPHRASE_FILE=" + os.environ["RELEASE_GPG_PASSPHRASE_FILE"]
            signing.run(["make", "release-build", "MAKEFLAGS=" + overrides])
            observations = Path(os.environ["BUILD_OBSERVATIONS"])
            self.assertTrue(observations.exists())
            observations.unlink()
            signing.run(["make", "release-build", "MAKEFLAGS=n " + overrides])
            self.assertFalse(observations.exists())

    def test_scoped_make_does_not_restore_private_sources_from_overrides(self):
        public = 'cash$$value space\tback\\slash "quoted" RELEASE_GPG_PRIVATE_KEY=public-lookalike\nnext'
        for variable in ("RELEASE_GPG_PRIVATE_KEY", "RELEASE_GPG_PRIVATE_KEY_FILE"):
            for operator in ("=", ":="):
                with self.subTest(variable=variable, operator=operator), self.make_repository():
                    os.environ.pop("RELEASE_GPG_PRIVATE_KEY", None)
                    os.environ.pop("RELEASE_GPG_PRIVATE_KEY_FILE", None)
                    private_file = Path("private key export.asc").resolve()
                    private_file.write_text(self.private.read_text())
                    value = self.private.read_text() if variable == "RELEASE_GPG_PRIVATE_KEY" else str(private_file)
                    marker = next(line for line in self.private.read_text().splitlines() if len(line) > 60)
                    Path("scope_probe.py").write_text(
                        "import json, os\nfrom pathlib import Path\n"
                        "Path('scope_probe.json').write_text(json.dumps({\n"
                        "'private_source': any(k in os.environ for k in "
                        "('RELEASE_GPG_PRIVATE_KEY', 'RELEASE_GPG_PRIVATE_KEY_FILE')),\n"
                        "'serialized_source': any(source in os.environ.get(k, '') "
                        "for source in " + repr((marker, str(private_file).replace(" ", "\\ "))) +
                        " for k in ('MAKEFLAGS', 'MAKEOVERRIDES')),\n"
                        "'public': os.environ.get('PUBLIC'),\n"
                        "'public_origin': os.environ.get('PUBLIC_ORIGIN'),\n"
                        "'lookalike': os.environ.get('RELEASE_GPG_PRIVATE_KEY_EXTRA'),\n"
                        "'lookalike_origin': os.environ.get('LOOKALIKE_ORIGIN'),\n"
                        "'version': os.environ.get('RELEASE_VERSION'),\n"
                        "'execution_flag': '--no-print-directory' in os.environ.get('MAKEFLAGS', '')\n"
                        "}))\n")
                    makefile = Path("Makefile")
                    makefile.write_text(makefile.read_text() +
                                        "\nPUBLIC = child-default\n"
                                        "RELEASE_GPG_PRIVATE_KEY_EXTRA = child-lookalike-default\n"
                                        "export PUBLIC RELEASE_GPG_PRIVATE_KEY_EXTRA\n"
                                        ".PHONY: scope-baseline scope-probe _scope-inspect _scope-probe\n"
                                        "scope-baseline:\n\t+$(MAKE) -f \"$(RELEASE_MAKEFILE)\" _scope-inspect\n"
                                        "scope-probe:\n\t+python3 \"$(RELEASE_SIGNING_HELPER)\" exec "
                                        "$(MAKE) -f \"$(RELEASE_MAKEFILE)\" _scope-probe\n"
                                        "_scope-inspect:\n\tPUBLIC_ORIGIN=\"$(origin PUBLIC)\" "
                                        "LOOKALIKE_ORIGIN=\"$(origin RELEASE_GPG_PRIVATE_KEY_EXTRA)\" "
                                        "python3 scope_probe.py\n"
                                        "_scope-probe: _scope-inspect\n\tgit commit --quiet --allow-empty -m 'Scoped Make check'\n")
                    arguments = ["make", "--no-print-directory", "PUBLIC:=" + public,
                                 "RELEASE_GPG_PRIVATE_KEY_EXTRA=public-prefix-value",
                                 "RELEASE_VERSION=9.8.7", variable + operator + value]
                    # Compare with native recursive Make, including its dollar expansion.
                    signing.run([*arguments, "scope-baseline"])
                    baseline = json.loads(Path("scope_probe.json").read_text())
                    self.assertTrue(baseline["private_source"])
                    self.assertTrue(baseline["serialized_source"])
                    self.assertEqual(baseline["public_origin"], "command line")
                    self.assertNotEqual(baseline["public"], "child-default")
                    self.assertEqual(baseline["lookalike_origin"], "command line")
                    self.assertEqual(baseline["lookalike"], "public-prefix-value")
                    self.assertEqual(baseline["version"], "9.8.7")
                    self.assertTrue(baseline["execution_flag"])
                    signing.run([*arguments, "scope-probe"])
                    observed = json.loads(Path("scope_probe.json").read_text())
                    self.assertEqual(observed, {**baseline, "private_source": False,
                                                "serialized_source": False})
                    signing.verify_commit("HEAD")

    def test_failed_build_or_signing_stops_before_publication(self):
        for failure in ("build", "signing"):
            with self.subTest(failure=failure), self.make_repository() as directory:
                if failure == "build":
                    os.environ["FAIL_BUILD"] = "1"
                else:
                    Path(os.environ["RELEASE_GPG_PASSPHRASE_FILE"]).write_text("incorrect-passphrase\n")
                result = subprocess.run(["make", "-j2", "release", "NUGET_PUBLISH_TOKEN=test-only-publishing-key"],
                                        capture_output=True, text=True, timeout=60)
                self.assertNotEqual(result.returncode, 0)
                commands = [json.loads(line) for line in Path(os.environ["BUILD_OBSERVATIONS"]).read_text().splitlines()]
                self.assertFalse(any(command[:2] == ["nuget", "push"] for command in commands))
                if failure == "build":
                    self.assertNotIn("signing.py\" exec ", result.stdout)
                    self.assertFalse(Path("nupkgs/SHA256SUMS").exists())
                else:
                    self.assertIn("GPG signature creation failed", result.stderr)
                    self.assertTrue(Path("nupkgs/SHA256SUMS").exists())
                self.assertFalse(list(Path("nupkgs").glob("*.asc")))
                self.assertFalse(list(directory.glob("release-signing-*")))


class SignatureStatusTests(unittest.TestCase):
    def test_validsig_never_overrides_nonzero_gnupg_status(self):
        valid = "[GNUPG:] VALIDSIG " + signing.SIGNING + " 2026-10-20 1 0 4 0 1 10 00 " + signing.PRIMARY
        result = subprocess.CompletedProcess([], 1, valid, "")
        with patch.object(subprocess, "run", return_value=result), self.assertRaises(signing.SigningError):
            signing.verify_status(signing.gpg("--status-fd", "1", "--verify", "test.asc", "test"))

    def test_expected_signer_preserves_gnupg_expiry_and_goodsig_behavior(self):
        valid = "[GNUPG:] VALIDSIG " + signing.SIGNING + " 2026-10-20 1 0 4 0 1 10 00 " + signing.PRIMARY
        signing.verify_status(valid)
        for code in ("GOODSIG", "EXPKEYSIG", "EXPSIG", "KEYEXPIRED", "SIGEXPIRED"):
            with self.subTest(code=code):
                signing.verify_status("[GNUPG:] " + code + "\n" + valid)

    def test_wrong_ambiguous_and_revoked_signatures_rejected(self):
        valid = "[GNUPG:] VALIDSIG " + signing.SIGNING + " 2026-10-20 1 0 4 0 1 10 00 " + signing.PRIMARY
        valid = "[GNUPG:] GOODSIG " + signing.SIGNING[-16:] + " Release signer\n" + valid
        for status in [valid.replace(signing.SIGNING, "0" * 40),
                       valid.replace(signing.PRIMARY, "0" * 40), valid + "\n" + valid,
                       "[GNUPG:] REVKEYSIG " + signing.SIGNING + "\n" + valid,
                       valid + "\n[GNUPG:] KEYREVOKED"]:
            with self.subTest(status=status), self.assertRaises(signing.SigningError):
                signing.verify_status(status)


class BuildEnvironmentTests(unittest.TestCase):
    def test_makeflags_separator_and_execution_flags(self):
        for flags, expected in [
            ("-- RELEASE_GPG_PASSPHRASE=secret", ""),
            (" -- RELEASE_GPG_PASSPHRASE=secret", ""),
            ("n -- RELEASE_GPG_PASSPHRASE=secret", "n"),
            ("s -j2 --jobserver-auth=3,4 -- RELEASE_GPG_PASSPHRASE=secret",
             "s -j2 --jobserver-auth=3,4"),
            ("s --warn-undefined-variables", "s --warn-undefined-variables"),
        ]:
            with self.subTest(flags=flags), patch.dict(os.environ, {"MAKEFLAGS": flags}, clear=True):
                self.assertEqual(signing.build_environment()["MAKEFLAGS"], expected)


class DiagnosticTests(unittest.TestCase):
    def test_passphrase_diagnostic_states_existing_utf8_byte_limit(self):
        for value in ("a" * 1024, "é" * 512):
            with self.subTest(bytes=len(value.encode())):
                self.assertEqual(signing.passphrase_value({"RELEASE_GPG_PASSPHRASE": value}), value)
        for value in ("a" * 1025, "é" * 512 + "x"):
            with self.subTest(bytes=len(value.encode())), \
                    self.assertRaisesRegex(signing.SigningError, "at most 1024 UTF-8 bytes") as error:
                signing.passphrase_value({"RELEASE_GPG_PASSPHRASE": value})
            self.assertNotIn(value, str(error.exception))

    def test_failed_commands_identify_operations_without_unsafe_values(self):
        sentinel = "DO-NOT-DISCLOSE"
        result = subprocess.CompletedProcess([], 17, sentinel, sentinel)
        with patch.object(subprocess, "run", return_value=result):
            with self.assertRaisesRegex(signing.SigningError, r"Git verify-commit failed \(exit status 17\)") as error:
                signing.run(["git", "verify-commit", sentinel])
            self.assertNotIn(sentinel, str(error.exception))
            with self.assertRaisesRegex(signing.SigningError, r"GPG key import failed \(exit status 17\)") as error:
                signing.credential_gpg(["--import", sentinel], payload=sentinel,
                                       env={"RELEASE_GPG_PASSPHRASE": sentinel})
            self.assertNotIn(sentinel, str(error.exception))

    def test_command_start_and_timeout_errors_withhold_payloads(self):
        for failure, category in [(OSError("DO-NOT-DISCLOSE"), "OS error"),
                                  (subprocess.TimeoutExpired("DO-NOT-DISCLOSE", 60,
                                                             output="DO-NOT-DISCLOSE"), "timed out")]:
            with self.subTest(category=category), patch.object(subprocess, "run", side_effect=failure):
                with self.assertRaises(signing.SigningError) as error:
                    signing.gpg("--verify", "DO-NOT-DISCLOSE")
                self.assertIn("GPG signature verification", str(error.exception))
                self.assertIn(category, str(error.exception))
                self.assertNotIn("DO-NOT-DISCLOSE", str(error.exception))

    def test_unexpected_failure_identifies_operation_and_safe_category(self):
        for failure, category in [(OSError("DO-NOT-DISCLOSE"), "OS error"),
                                  (RuntimeError("DO-NOT-DISCLOSE"), "internal error")]:
            output = StringIO()
            with patch("sys.argv", ["signing.py", "verify-commit", "HEAD"]), \
                    patch.object(signing, "verify_commit", side_effect=failure), redirect_stderr(output):
                self.assertEqual(signing.main(), 1)
            self.assertIn("verify-commit failed (" + category + ")", output.getvalue())
            self.assertNotIn("DO-NOT-DISCLOSE", output.getvalue())


class ReleaseUserTests(unittest.TestCase):
    def test_actions_release_requires_publisher_triggering_user_including_reruns(self):
        for actor, rerun, allowed in [("scylladb-publisher", "", True),
                                      ("scylladb-publisher", "scylladb-publisher", True),
                                      ("other-maintainer", "scylladb-publisher", True),
                                      ("other-maintainer", "", False),
                                      ("scylladb-publisher", "other-maintainer", False),
                                      ("", "", False)]:
            with self.subTest(actor=actor, rerun=rerun):
                result = subprocess.run(["make", "release-check-user"], cwd=ROOT,
                                        capture_output=True, text=True, timeout=60,
                                        env={**os.environ, "GITHUB_ACTIONS": "true",
                                             "GITHUB_ACTOR": actor, "GITHUB_TRIGGERING_ACTOR": rerun})
                self.assertEqual(result.returncode == 0, allowed, result.stderr)

    def test_actions_dry_run_rejects_other_triggering_user_before_preparation(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "packages"
            result = subprocess.run(["make", "release-dry-run"], cwd=ROOT,
                                    capture_output=True, text=True, timeout=60,
                                    env={**os.environ, "GITHUB_ACTIONS": "true",
                                         "GITHUB_ACTOR": "scylladb-publisher",
                                         "GITHUB_TRIGGERING_ACTOR": "other-maintainer",
                                         "PACKAGE_OUTPUT_DIR": str(output)})
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Release safety check failed", result.stderr)
            self.assertNotIn("release-prepare", result.stdout)
            self.assertFalse(output.exists())

    def test_publication_job_uses_release_environment(self):
        workflow = (ROOT / ".github/workflows/release.yml").read_text()
        self.assertIn("    environment: release\n", workflow)
        self.assertNotIn("Require publisher release user", workflow)

    def test_local_release_uses_authenticated_gh_user(self):
        with tempfile.TemporaryDirectory() as temp:
            gh = Path(temp) / "gh"
            gh.write_text('#!/bin/sh\nprintf "%s\\n" "$TEST_GH_USER"\n')
            gh.chmod(0o700)
            env = {**os.environ, "PATH": temp + os.pathsep + os.environ["PATH"],
                   "GITHUB_ACTIONS": "false", "TEST_GH_USER": "someone-else"}
            result = subprocess.run(["make", "release-prepare"], cwd=ROOT, env=env,
                                    capture_output=True, text=True, timeout=60)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Release safety check failed", result.stderr)
            env["TEST_GH_USER"] = "scylladb-publisher"
            subprocess.run(["make", "release-check-user"], cwd=ROOT, env=env, check=True,
                           capture_output=True, timeout=60)
            gh.write_text("#!/bin/sh\nexit 1\n")
            result = subprocess.run(["make", "release-prepare"], cwd=ROOT, env=env,
                                    capture_output=True, text=True, timeout=60)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Release safety check failed", result.stderr)


if __name__ == "__main__":
    unittest.main()
