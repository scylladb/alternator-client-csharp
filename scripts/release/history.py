#!/usr/bin/env python3
"""Prepare and verify release history without executing project code or Git hooks."""

import json
import os
from pathlib import Path
import re
import sys
import tempfile
import xml.etree.ElementTree as ET

import signing

PROJECT = "ScyllaDB.Alternator.csproj"
VERSION = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?"


def git(*args, **kwargs):
    return signing.run(["git", "-c", "core.hooksPath=/dev/null", *args], **kwargs).strip()


def manifest_path():
    return Path(os.environ.get("PACKAGE_OUTPUT_DIR", "nupkgs")) / "release-history.json"


def versioned_project(original, version, development=False):
    root = ET.fromstring(original)
    prefixes = root.findall("./PropertyGroup/VersionPrefix")
    if (len(prefixes) != 1 or prefixes[0].attrib
            or root.findall(".//Version") or root.findall(".//PackageVersion")
            or len(root.findall(".//VersionPrefix")) != 1
            or len(root.findall(".//VersionSuffix")) > 1):
        raise signing.SigningError("Release history requires one unconditional VersionPrefix and no version overrides.")
    group = next(group for group in root.findall("PropertyGroup") if prefixes[0] in list(group))
    if group.attrib or any(item.attrib for item in root.findall(".//VersionSuffix")):
        raise signing.SigningError("Conditional project versions are unsupported.")
    text = original.decode("utf-8")
    text, count = re.subn(r"<VersionPrefix>[^<]*</VersionPrefix>",
                         "<VersionPrefix>" + version + "</VersionPrefix>", text)
    if count != 1:
        raise signing.SigningError("Unsupported project version layout.")
    text = re.sub(r"[ \t]*<VersionSuffix>[^<]*</VersionSuffix>\r?\n?", "", text)
    if development:
        indent = re.search(r"(?m)^([ \t]*)<VersionPrefix>", text)
        separator = ("\r\n" if "\r\n" in text else "\n") + indent[1] if indent else ""
        text = text.replace("</VersionPrefix>", "</VersionPrefix>" + separator
                            + "<VersionSuffix>dev</VersionSuffix>", 1)
    return text.encode("utf-8")


def tree_with_project(baseline, project):
    with tempfile.TemporaryDirectory(prefix="release-index-") as directory:
        env = {**signing.build_environment(), "GIT_INDEX_FILE": str(Path(directory) / "index")}
        git("read-tree", baseline, env=env)
        blob = git("hash-object", "-w", "--stdin", env=env, input=project.decode("utf-8"))
        git("update-index", "--add", "--cacheinfo", "100644," + blob + "," + PROJECT, env=env)
        return git("write-tree", env=env)


def plan_values(baseline=None, version=None, next_version=None):
    baseline = baseline or os.environ.get("RELEASE_BASE_COMMIT") or git("rev-parse", "HEAD")
    if not re.fullmatch(r"[0-9a-f]{40}", baseline):
        raise signing.SigningError("Release history requires the original commit SHA.")
    if git("ls-tree", baseline, "--", PROJECT).split()[:2] != ["100644", "blob"]:
        raise signing.SigningError("Release project must be a tracked regular file.")
    original = signing.run(["git", "show", baseline + ":" + PROJECT]).encode("utf-8")
    root = ET.fromstring(original)
    version = version or os.environ.get("RELEASE_VERSION") or root.findtext("./PropertyGroup/VersionPrefix", "")
    if not re.fullmatch(VERSION, version):
        raise signing.SigningError("Release history requires a semantic package version without build metadata.")
    parts = version.split("-", 1)[0].split(".")
    next_version = next_version or os.environ.get("RELEASE_NEXT_VERSION") or \
        ".".join([parts[0], parts[1], str(int(parts[2]) + 1)])
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", next_version):
        raise signing.SigningError("Next development version must be a numeric semantic version.")
    if tuple(map(int, next_version.split("."))) <= tuple(map(int, parts)):
        raise signing.SigningError("Next development version must follow the release version.")
    return {"baseline": baseline, "version": version, "next-version": next_version,
            "tag": version, "release-tree": tree_with_project(baseline, versioned_project(original, version)),
            "development-tree": tree_with_project(baseline, versioned_project(original, next_version, True))}


def write_manifest(values):
    path = manifest_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, delete=False) as output:
        temporary = Path(output.name)
        try:
            json.dump(values, output, sort_keys=True)
            output.write("\n")
            output.flush()
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)


def read_manifest():
    values = json.loads(manifest_path().read_text())
    expected = plan_values(values["baseline"], values["version"], values["next-version"])
    if any(values.get(key) != value for key, value in expected.items()):
        raise signing.SigningError("Release history metadata does not match the source.")
    baseline = os.environ.get("RELEASE_BASE_COMMIT")
    if baseline and baseline != values["baseline"]:
        raise signing.SigningError("Release history baseline changed.")
    expected_tree = os.environ.get("RELEASE_EXPECTED_TREE")
    if expected_tree and expected_tree != values["release-tree"]:
        raise signing.SigningError("Release tree differs from the validated build.")
    return values


def plan():
    git("diff-index", "--quiet", "HEAD", "--")
    values = plan_values()
    expected_tree = os.environ.get("RELEASE_EXPECTED_TREE")
    if expected_tree and values["release-tree"] != expected_tree:
        raise signing.SigningError("Release tree differs from the validated build.")
    for ref in ("refs/tags/" + values["tag"], "refs/heads/release-prepared/" + values["version"]):
        result = signing.invoke(["git", "show-ref", "--verify", "--quiet", ref])
        if result.returncode != 1:
            raise signing.SigningError("Release version already has a local tag or prepared branch; use a new version.")
    write_manifest(values)
    for key in ("version", "next-version", "release-tree"):
        print(key + "=" + values[key])


def create():
    values = read_manifest()
    if "development-commit" in values:
        verify()
        retain_bundle(values)
        return
    tag = values["tag"]
    branch = "refs/heads/release-prepared/" + values["version"]
    for ref in ("refs/tags/" + tag, branch):
        if signing.invoke(["git", "show-ref", "--verify", "--quiet", ref]).returncode != 1:
            raise signing.SigningError("Existing release refs must not be replaced.")
    release = git("commit-tree", "-S", values["release-tree"], "-p", values["baseline"],
                  "-m", "Release " + values["version"])
    development = git("commit-tree", "-S", values["development-tree"], "-p", release,
                      "-m", "Start " + values["next-version"] + " development")
    tag_payload = ("object " + release + "\ntype commit\ntag " + tag + "\ntagger "
                   + git("var", "GIT_COMMITTER_IDENT") + "\n\nRelease " + values["version"] + "\n")
    signature = signing.credential_gpg(["--local-user", signing.SIGNING + "!", "--armor", "--detach-sign"],
                                       payload=tag_payload)
    tag_object = git("mktag", input=tag_payload + signature)
    # Publish both local refs in one transaction; collisions leave both unchanged.
    git("update-ref", "--stdin", input="start\ncreate refs/tags/" + tag + " " + tag_object
        + "\ncreate " + branch + " " + development + "\nprepare\ncommit\n")
    values.update({"release-commit": release, "development-commit": development})
    write_manifest(values)
    verify()
    retain_bundle(values)


def retain_bundle(values):
    bundle = manifest_path().with_suffix(".bundle").resolve()
    if not bundle.exists():
        git("bundle", "create", str(bundle), "refs/heads/release-prepared/" + values["version"],
            "refs/tags/" + values["tag"])
    git("bundle", "verify", str(bundle))


def verify():
    values = read_manifest()
    release, development = values["release-commit"], values["development-commit"]
    if any(not re.fullmatch(r"[0-9a-f]{40}", commit) for commit in (release, development)):
        raise signing.SigningError("Invalid prepared commit IDs.")
    git("merge-base", "--is-ancestor", values["baseline"], development)
    if (git("rev-list", "--reverse", values["baseline"] + ".." + development).splitlines() != [release, development]
            or git("rev-parse", release + "^@") != values["baseline"]
            or git("rev-parse", development + "^@") != release
            or git("rev-parse", release + "^{tree}") != values["release-tree"]
            or git("rev-parse", development + "^{tree}") != values["development-tree"]
            or git("rev-parse", "refs/heads/release-prepared/" + values["version"]) != development):
        raise signing.SigningError("Prepared history does not match the planned commits and trees.")
    for commit in (release, development):
        signing.verify_commit(commit)
    signing.verify_tag(values["tag"], release)
    return values


def push():
    values = verify()
    if os.environ.get("RELEASE_SOURCE_REF", "heads/main") != "heads/main":
        print("Historical release: prepared history remains local.")
        return
    # The explicit lease rejects an advanced or rewritten main; both refs update atomically.
    git("push", "--atomic", "--force-with-lease=refs/heads/main:" + values["baseline"], "origin",
        values["development-commit"] + ":refs/heads/main", "refs/tags/" + values["tag"])


def main():
    import guard
    try:
        guard.authorize_operator()
        guard.check_inputs()
        command = sys.argv[1:]
        if command == ["plan"]:
            plan()
        elif command == ["version"]:
            print(read_manifest()["version"])
        elif command == ["create"]:
            create()
        elif command == ["verify"]:
            verify()
        elif command == ["push"]:
            push()
        else:
            raise signing.SigningError("Expected plan, version, create, verify, or push.")
        return 0
    except Exception:
        print("Release history check failed; values withheld.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
