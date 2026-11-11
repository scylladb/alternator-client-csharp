#!/usr/bin/env python3
"""Create generated GitHub notes only after the authoritative tag is published."""

import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from urllib.parse import quote

from history import VERSION


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def api(endpoint, *args):
    return json.loads(run('gh', 'api', endpoint, *args))


def validate_source():
    source = os.environ['RELEASE_SOURCE_REF']
    if source != 'heads/main':
        raise ValueError('New package publication requires main; use notes-only recovery for published versions')
    return source


def preserve_release(existing, tag):
    if (existing.get('tag_name') != tag or existing.get('draft') is not False
            or not existing.get('published_at')):
        raise RuntimeError('Existing GitHub release is unpublished or does not match the version tag')
    print('Published GitHub release already exists; preserving its notes and assets.')


def publish(manifest=None, expected=None):
    repository = os.environ['GITHUB_REPOSITORY']
    if manifest is None:
        manifest = json.loads((Path(os.environ['PACKAGE_OUTPUT_DIR']) / 'release-history.json').read_text())
    tag = manifest['tag']
    if not re.fullmatch(VERSION, tag):
        raise ValueError('Invalid release tag')
    if expected is None:
        validate_source()
        expected = run('git', 'rev-parse', '--verify', 'refs/tags/' + tag)
    if not re.fullmatch(r'[0-9a-f]{40}', expected):
        raise ValueError('Invalid authoritative tag identity')
    remote = api(f'repos/{repository}/git/ref/tags/{quote(tag, safe="")}')
    if remote.get('ref') != 'refs/tags/' + tag or remote.get('object', {}).get('sha') != expected:
        raise ValueError('Authoritative release tag is missing or changed')
    # Query by tag without treating authentication, network, or server errors as absence.
    endpoint = f'repos/{repository}/releases/tags/{quote(tag, safe="")}'
    result = subprocess.run(['gh', 'api', '--include', endpoint], text=True, capture_output=True, check=False)
    if result.returncode == 0:
        existing = json.loads(re.split(r'\r?\n\r?\n', result.stdout, maxsplit=1)[-1])
        preserve_release(existing, tag)
        return
    if not re.match(r'HTTP/\S+ 404(?: |\n|\r)', result.stdout):
        raise RuntimeError('Cannot check existing GitHub release')
    # Tag lookup only returns published releases. Users with push permission also
    # see drafts in the paginated listing; a 404 above alone does not prove absence.
    pages = api(f'repos/{repository}/releases?per_page=100', '--paginate', '--slurp')
    if (not isinstance(pages, list) or not pages
            or any(not isinstance(page, list) for page in pages)
            or any(not isinstance(release, dict) or not isinstance(release.get('tag_name'), str)
                   for page in pages for release in page)):
        raise RuntimeError('Cannot verify the complete existing-release listing')
    matches = [release for page in pages for release in page if release['tag_name'] == tag]
    if matches:
        for existing in matches:
            preserve_release(existing, tag)
        return
    # Use the shared omission label when GitHub generates release notes.
    notes = api(f'repos/{repository}/releases/generate-notes', '-X', 'POST',
                '-f', 'tag_name=' + tag, '-f', 'configuration_file_path=.github/release.yml')
    api(f'repos/{repository}/releases', '-X', 'POST', '-f', 'tag_name=' + tag,
        '-f', 'name=' + tag, '-f', 'body=' + notes['body'],
        '-F', 'prerelease=' + str('-' in tag).lower(), '-f', 'make_latest=legacy')


def recover():
    """Verify retained signed history in isolation; never prepare or publish packages."""
    import guard
    import history
    import signing

    guard.authorize_operator()
    output = Path(os.environ['PACKAGE_OUTPUT_DIR']).resolve()
    manifest = output / 'release-history.json'
    bundle = output / 'release-history.bundle'
    signature = output / 'release-history.json.asc'
    if any(path.is_symlink() or not path.is_file() for path in (manifest, signature, bundle)):
        raise ValueError('Recovery requires the retained manifest, signature, and Git bundle')
    with signing.public_environment() as env:
        signing.verify_status(signing.gpg('--status-fd', '1', '--verify', signature, manifest, env=env))
    values = json.loads(manifest.read_text())
    tag = values['tag']
    if not re.fullmatch(VERSION, tag):
        raise ValueError('Invalid recovery version tag')
    original = Path.cwd()
    with tempfile.TemporaryDirectory(prefix='github-release-recovery-') as directory:
        try:
            run('git', '-c', 'core.hooksPath=/dev/null', 'init', '--quiet', directory)
            os.chdir(directory)
            run('git', '-c', 'core.hooksPath=/dev/null', 'fetch', '--quiet', '--no-tags', str(bundle),
                'refs/tags/' + tag + ':refs/tags/' + tag,
                'refs/heads/release-prepared/' + tag + ':refs/heads/release-prepared/' + tag)
            # This reconstructs the planned trees and verifies both commits and the annotated tag.
            # The signed identities authenticate bundle contents without executing project code.
            previous_output = os.environ.get('PACKAGE_OUTPUT_DIR')
            try:
                os.environ['PACKAGE_OUTPUT_DIR'] = str(output)
                verified = history.verify()
            finally:
                if previous_output is None:
                    os.environ.pop('PACKAGE_OUTPUT_DIR', None)
                else:
                    os.environ['PACKAGE_OUTPUT_DIR'] = previous_output
            expected = run('git', 'rev-parse', '--verify', 'refs/tags/' + tag)
            publish(verified, expected)
        finally:
            os.chdir(original)


if __name__ == '__main__':
    import sys
    if sys.argv[1:] == ['check-source']:
        validate_source()
    elif sys.argv[1:] == ['recover']:
        recover()
    elif not sys.argv[1:]:
        publish()
    else:
        raise ValueError('Expected check-source, recover, or no arguments')
