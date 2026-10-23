#!/usr/bin/env python3
"""Validate release inputs, verify publication bytes, and retain sanitized logs."""

import base64
import hashlib
import heapq
import html
import json
import os
from pathlib import Path
import re
import selectors
import signal
import subprocess
import sys
import time
import urllib.parse
import xml.etree.ElementTree as ET

import signing

LIMIT = 8 * 1024 * 1024
DRAIN_GRACE = 1
OVERFLOW_LOG = b"Release output exceeded capture limit; output withheld.\n"
INCOMPLETE_LOG = b"Release output incomplete; output withheld.\n"
FAILED_LOG = b"Release output unavailable; values withheld.\n"
CANCELLED_LOG = b"Release cancelled; incomplete output withheld.\n"


class Unsafe(ValueError):
    pass


def authorize_operator():
    if os.environ.get("GITHUB_ACTIONS") == "true":
        actor = os.environ.get("GITHUB_TRIGGERING_ACTOR") or os.environ.get("GITHUB_ACTOR", "")
        if actor != "scylladb-publisher":
            raise Unsafe("Actions release requires the configured publisher triggering user")
        return
    if signing.run(["gh", "api", "user", "--jq", ".login"]).strip() != "scylladb-publisher":
        raise Unsafe("local publication requires the configured publisher account")


def check_environment():
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise Unsafe("missing workflow repository")
    endpoint = "repos/" + repository + "/environments/release"
    environment = json.loads(signing.run(["gh", "api", endpoint]))
    approvals = [rule for rule in environment.get("protection_rules", [])
                 if rule.get("type") == "required_reviewers"]
    reviewers = approvals[0].get("reviewers", []) if len(approvals) == 1 else []
    reviewer = reviewers[0].get("reviewer", {}) if len(reviewers) == 1 else {}
    policy = environment.get("deployment_branch_policy") or {}
    if (environment.get("can_admins_bypass") is not False or len(approvals) != 1
            or approvals[0].get("prevent_self_review") is not True
            or len(reviewers) != 1 or reviewers[0].get("type") != "Team"
            or reviewer.get("slug") != "drivers"
            or reviewer.get("html_url") != "https://github.com/orgs/scylladb/teams/drivers"
            or policy.get("custom_branch_policies") is not True
            or policy.get("protected_branches") is not False):
        raise Unsafe("release environment requires only scylladb/drivers approval, no self-review/bypass, and main-only launch refs")
    # A second page means more allowed branches than the one supported here.
    rules = json.loads(signing.run(["gh", "api", endpoint + "/deployment-branch-policies?per_page=100"]))
    branches = rules.get("branch_policies", [])
    if (rules.get("total_count") != 1 or len(branches) != 1
            or branches[0].get("name") != "main" or branches[0].get("type") != "branch"):
        raise Unsafe("release environment must allow only the main branch")


def check_inputs():
    version = os.environ.get("RELEASE_VERSION", "")
    if version and not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?", version):
        raise Unsafe("invalid release version")
    if os.environ.get("RELEASE_SKIP_TESTS", "false") not in ("true", "false", "1", "0", ""):
        raise Unsafe("invalid test control")
    if os.environ.get("DOTNET_VERBOSITY", "normal") not in ("quiet", "minimal", "normal"):
        raise Unsafe("debug release output is disabled")
    source = urllib.parse.urlsplit(os.environ.get("NUGET_SOURCE", "https://api.nuget.org/v3/index.json"))
    if source.scheme != "https" or not source.hostname or source.username or source.password or source.query or source.fragment:
        raise Unsafe("NuGet source must be an HTTPS URL without credentials")
    # These values also enter existing shell recipes. Reject shell syntax before Make starts.
    for name in ("PACKAGE_OUTPUT_DIR", "NUGET_SOURCE"):
        if not re.fullmatch(r"[A-Za-z0-9_./:@%+=,-]+", os.environ.get(name, "nupkgs")):
            raise Unsafe("unsupported release path or source")
    for name in ("MSBUILDDEBUGENGINE", "MSBUILDDEBUGPATH", "MSBUILDDEBUGCOMM", "MSBuildEmitSolution"):
        if os.environ.get(name):
            raise Unsafe("MSBuild debug output is disabled")


def check_command(command):
    # Validate before launch; never echo option contents in diagnostics.
    for index, argument in enumerate(command[1:], 1):
        if (re.search(r"(?i)(?:^[-/](?:p|property):|^--?)(?:[^=]*(?:password|passphrase|token|secret|api[-_]?key|authorization)[^=]*)(?:=|$)", argument)
                or re.match(r"(?i)^[-/](?:bl|binarylogger|fl|filelogger)(?:[:=0-9]|$)", argument)
                or re.match(r"(?i)^(?:[-/]v(?:erbosity)?[:=]|--verbosity=)(?:diag(?:nostic)?|detailed)$", argument)
                or argument.lower() in ("--debug", "-x")):
            raise Unsafe("debug logging or credential-bearing command properties are disabled")
        if argument.lower() in ("--verbosity", "-v") and index + 1 < len(command):
            if command[index + 1].lower() in ("diag", "diagnostic", "detailed"):
                raise Unsafe("debug logging is disabled")


def check_target():
    # Historical releases use the current retained Makefile and helpers. Validate
    # the minimum supported project policy without executing historical build code.
    project = ET.parse("ScyllaDB.Alternator.csproj").getroot()
    if (project.attrib.get("Sdk") != "Microsoft.NET.Sdk"
            or project.findtext(".//TargetFramework") != "net8.0"
            or project.findtext(".//PackageId") != "ScyllaDB.Alternator"):
        raise Unsafe("release target must be the supported .NET 8 Alternator package")


def secret_forms(env):
    values = {
        v
        for k, v in env.items()
        if v and re.search(r"PASSWORD|PASSPHRASE|TOKEN|SECRET|PRIVATE_KEY|API_KEY|AUTHORIZATION", k, re.I)
    }
    for name in ("RELEASE_GPG_PRIVATE_KEY_FILE", "RELEASE_GPG_PASSPHRASE_FILE"):
        if env.get(name):
            values.add(Path(env[name]).read_text().removesuffix("\n"))
    # Include multiline components, escaped newlines, URL and both Base64 alphabets.
    values |= {line for value in values for line in value.splitlines() if line}
    values |= {value.replace("\n", "\\n").replace("\r", "\\r") for value in values}
    # JSON/logging serializers also escape quotes, backslashes and Unicode secrets.
    values |= {json.dumps(value, ensure_ascii=ascii_only)[1:-1]
               for value in tuple(values) for ascii_only in (True, False)}
    # MSBuild XML serializers escape punctuation and may use named or numeric quotes.
    for value in tuple(values):
        escaped = html.escape(value, quote=False)
        for apostrophe in ("'", "&apos;", "&#39;", "&#x27;"):
            for quote in ('"', "&quot;", "&#34;", "&#x22;"):
                values.add(escaped.replace("'", apostrophe).replace('"', quote))
    forms = set(values)
    for value in values:
        raw = value.encode()
        forms.update(
            (
                base64.b64encode(raw).decode(),
                base64.urlsafe_b64encode(raw).decode(),
                urllib.parse.quote(value, safe=""),
                urllib.parse.quote(value),
                urllib.parse.quote_plus(value, safe=""),
            )
        )
    for value in tuple(forms):
        forms.add(urllib.parse.quote(value, safe=""))
        forms.add(urllib.parse.quote(value))
        forms.add(value.rstrip("="))
    return sorted((v.encode() for v in forms if v), key=len, reverse=True)


def redact(data, forms):
    # Match every source against the original bytes so replacements cannot hide secrets.
    def spans(pattern, group, flags=0):
        return (match.span(group) for match in re.finditer(pattern, data, flags))

    matches = [
        spans(
            rb"(?i)([\"'])((?:-D\s*|--?define[=\s]+)[\w.-]*(?:passphrase|password|username|token|secret|authorization)[\w.-]*=)"
            rb"((?:\\[\s\S]|(?!\1)[^\\])*)\1", 3,
        ),
        spans(
            rb"(?i)((?:-D\s*|--?define[=\s]+)[\w.-]*(?:passphrase|password|username|token|secret|authorization)[\w.-]*=)"
            rb"(\"(?:\\[\s\S]|[^\"\\])*\"|'(?:\\[\s\S]|[^'\\])*'|[^\s]+)", 2,
        ),
        spans(rb"(?im)((?:proxy-)?authorization[\"']?\s*[:=]\s*)([^\r\n]*)", 2),
    ]
    for value in forms:
        # Lookahead includes overlapping occurrences; percent escapes ignore case.
        matches.append(spans(b"(?=(" + re.escape(value) + b"))", 1, re.I if b"%" in value else 0))
    output = bytearray()
    start = end = 0
    for left, right in heapq.merge(*matches):
        if left > end:
            if end > start:
                output.extend(b"[REDACTED]")
            output.extend(data[end:left])
            start = left
        end = max(end, right)
    if end > start:
        output.extend(b"[REDACTED]")
    output.extend(data[end:])
    return bytes(output)


def write_logs(clean, logs):
    """Write only sanitized content or fixed diagnostics, tolerating broken outputs."""
    output_failed = False
    for name, data, stream in zip(("stdout", "stderr"), clean, (sys.stdout, sys.stderr)):
        try:
            logs.mkdir(parents=True, exist_ok=True)
            with (logs / (name + ".log")).open("ab") as output:
                output.write(data)
        except OSError:
            output_failed = True
        try:
            stream.buffer.write(data)
            stream.flush()
        except OSError:
            output_failed = True
            # Prevent a failed shutdown flush from replacing the build exit status.
            try:
                with open(os.devnull, "wb") as sink:
                    os.dup2(sink.fileno(), stream.fileno())
            except OSError:
                pass
    if output_failed:
        try:
            os.write(2, b"Sanitized release output could not be fully written; values withheld.\n")
        except OSError:
            pass


def capture(command, env, forms, logs, interrupted):
    """Never spool raw output or sanitize a stream cut off before EOF."""
    interrupted.check()
    process = subprocess.Popen(
        command, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True
    )
    buffers = [bytearray(), bytearray()]
    complete = [False, False]
    overflow = False
    logs_started = False
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ, 0)
            selector.register(process.stderr, selectors.EVENT_READ, 1)
            exited_at = None
            while selector.get_map():
                interrupted.check()
                if process.poll() is not None:
                    if exited_at is None:
                        exited_at = time.monotonic()
                    elif time.monotonic() - exited_at >= DRAIN_GRACE:
                        break
                for key, _ in selector.select(timeout=0.1):
                    chunk = os.read(key.fileobj.fileno(), 65536)
                    if not chunk:
                        complete[key.data] = True
                        selector.unregister(key.fileobj)
                    elif not overflow:
                        if sum(map(len, buffers)) + len(chunk) > LIMIT:
                            overflow = True
                            buffers = [bytearray(), bytearray()]
                            continue
                        buffers[key.data].extend(chunk)
        while process.poll() is None:
            interrupted.check()
            time.sleep(0.05)
        status = process.wait()
        interrupted.check()
        # Sanitize both streams completely before opening artifacts or emitting output.
        try:
            clean = [OVERFLOW_LOG, OVERFLOW_LOG] if overflow else [
                redact(bytes(buffer), forms) if ended else INCOMPLETE_LOG
                for buffer, ended in zip(buffers, complete)
            ]
        except Exception:
            # Publishing has already exited. Withhold output without changing its status.
            clean = [FAILED_LOG, FAILED_LOG]
        logs_started = True
        write_logs(clean, logs)
        return status if status >= 0 else 128 - status
    except BaseException as error:
        completed_status = process.poll()
        signum = signal.SIGTERM
        if isinstance(error, SystemExit) and isinstance(error.code, int):
            signum = error.code - 128
        if signum not in (signal.SIGTERM, signal.SIGHUP, signal.SIGINT):
            signum = signal.SIGTERM
        try:
            os.killpg(process.pid, signum)
        except ProcessLookupError:
            pass
        try:
            process.wait(timeout=DRAIN_GRACE)
        except subprocess.TimeoutExpired:
            pass
        # The build parent may exit before a descendant responds to cancellation.
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
        clean = [FAILED_LOG, FAILED_LOG]
        if isinstance(error, (SystemExit, KeyboardInterrupt)):
            try:
                clean = [OVERFLOW_LOG, OVERFLOW_LOG] if overflow else [
                    redact(bytes(buffer), forms) if ended else CANCELLED_LOG
                    for buffer, ended in zip(buffers, complete)
                ]
            except Exception:
                pass
        if not logs_started:
            write_logs(clean, logs)
        if isinstance(error, Exception) and completed_status is not None:
            return completed_status if completed_status >= 0 else 128 - completed_status
        raise
    finally:
        process.stdout.close()
        process.stderr.close()


def verify_outputs():
    output = Path(os.environ.get("PACKAGE_OUTPUT_DIR", "nupkgs")).resolve()
    packages = sorted(output.glob("*.nupkg"))
    checksum = output / "SHA256SUMS"
    if not packages or any(not re.fullmatch(r"[A-Za-z0-9_.+-]+\.nupkg", p.name) for p in packages):
        raise Unsafe("expected release packages")
    artifacts = [*packages, checksum]
    if any(p.is_symlink() or not p.is_file() for p in artifacts):
        raise Unsafe("release artifacts must be regular files")
    with signing.public_environment() as env:
        for artifact in artifacts:
            signing.verify_status(signing.gpg("--status-fd", "1", "--verify",
                                             str(artifact) + ".asc", artifact, env=env))
    expected = []
    for package in packages:
        digest = hashlib.sha256()
        with package.open("rb") as stream:
            for chunk in iter(lambda: stream.read(65536), b""):
                digest.update(chunk)
        expected.append(digest.hexdigest() + "  " + package.name + "\n")
    expected = "".join(expected)
    if checksum.read_text() != expected:
        raise Unsafe("signed checksums do not match the complete package set")
    return packages


def checkout_target():
    target = os.environ.get("RELEASE_TARGET_TAG", "main") or "main"
    if target == "main":
        return
    # The workflow already checked out the selected branch or tag. Tag reruns
    # retain the established preceding-commit behavior without deleting any tag.
    ref = "refs/tags/" + target
    signing.run(["git", "check-ref-format", ref])
    result = signing.invoke(["git", "show-ref", "--verify", "--quiet", ref])
    if result.returncode == 0:
        if Path(__file__).resolve().is_relative_to(Path.cwd().resolve()):
            raise Unsafe("preserve current release tools outside the checkout before selecting history")
        commit = signing.run(["git", "rev-parse", "--verify", ref + "~1^{commit}"]).strip()
        signing.run(["git", "-c", "core.hooksPath=/dev/null", "checkout", "--detach", commit])
    elif result.returncode != 1:
        raise Unsafe("cannot resolve release target")


def main():
    try:
        with signing.session_signals() as interrupted:
            authorize_operator()
            if sys.argv[1:] == ["check-user"]:
                return 0
            if sys.argv[1:] == ["check-environment"]:
                check_environment()
                return 0
            if sys.argv[1:] == ["checkout-target"]:
                checkout_target()
                return 0
            check_inputs()
            if sys.argv[1:] == ["check-target"]:
                check_target()
                return 0
            logs = Path(os.environ.get("RELEASE_LOG_DIR", "/tmp/alternator-release-logs"))
            if sys.argv[1:] == ["publish"]:
                # Independently callable publication requires public verification;
                # it never imports private signing credentials.
                packages = verify_outputs()
                key = os.environ.get("NUGET_PUBLISH_TOKEN", "")
                if os.environ.get("GITHUB_ACTIONS") != "true":
                    raise Unsafe("NuGet publication requires the trusted-publisher workflow")
                if not key or "\n" in key or "\r" in key:
                    raise Unsafe("missing short-lived NuGet trusted-publisher token")
                if os.environ.get("NUGET_SOURCE", "https://api.nuget.org/v3/index.json") != "https://api.nuget.org/v3/index.json":
                    raise Unsafe("trusted publishing requires the NuGet.org source")
                forms = secret_forms(os.environ)
                for package in packages:
                    status = capture(["dotnet", "nuget", "push", str(package), "--api-key", key,
                                      "--source", os.environ.get("NUGET_SOURCE", "https://api.nuget.org/v3/index.json"),
                                      "--skip-duplicate"], signing.build_environment(), forms, logs, interrupted)
                    if status:
                        return status
                return 0
            if len(sys.argv) > 2 and sys.argv[1] == "run":
                check_command(sys.argv[2:])
                env = signing.build_environment()
                env["DOTNET_FORMAT_VERBOSITY"] = "normal"
                return capture(sys.argv[2:], env, secret_forms(os.environ), logs, interrupted)
            raise Unsafe("expected run COMMAND, publish, check-user, check-target, checkout-target, or check-environment")
    except (Unsafe, signing.SigningError):
        print("Release safety check failed; values withheld.", file=sys.stderr)
    except Exception:
        print("Release safety or sanitized log capture failed; values withheld.", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
