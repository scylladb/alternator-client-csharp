import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import github_release

ROOT = Path(__file__).resolve().parents[2]


class GitHubReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        (Path(self.temp.name) / 'release-history.json').write_text(json.dumps({'tag': '2.0.5'}))
        self.env = patch.dict(os.environ, {
            'GITHUB_REPOSITORY': 'scylladb/alternator-client-csharp',
            'GITHUB_SHA': 'c' * 40,
            'PACKAGE_OUTPUT_DIR': self.temp.name,
            'RELEASE_SOURCE_REF': 'heads/main',
            'RELEASE_SOURCE_REF_SHA': 'b' * 40,
        })
        self.env.start()
        self.addCleanup(self.env.stop)
        self.run = patch.object(github_release, 'run', return_value='a' * 40).start()
        self.api = patch.object(github_release, 'api', side_effect=[
            {'ref': 'refs/tags/2.0.5', 'object': {'sha': 'a' * 40}},
            [[]],
            {'body': 'Filtered notes'}, {},
        ]).start()
        self.request = patch.object(github_release.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, 'HTTP/2.0 404 Not Found\r\n', '')).start()
        self.addCleanup(patch.stopall)

    def test_creates_only_after_exact_remote_tag_and_uses_filter_config(self):
        github_release.publish()
        self.assertEqual(self.api.call_count, 4)
        self.assertEqual(self.api.call_args_list[1].args,
                         ('repos/scylladb/alternator-client-csharp/releases?per_page=100', '--paginate', '--slurp'))
        self.assertIn('configuration_file_path=.github/release.yml', self.api.call_args_list[2].args)
        self.assertIn('body=Filtered notes', self.api.call_args_list[3].args)
        self.assertIn('prerelease=false', self.api.call_args_list[3].args)
        self.assertIn('make_latest=legacy', self.api.call_args_list[3].args)

    def test_preserves_existing_release(self):
        existing = {'tag_name': '2.0.5', 'draft': False, 'published_at': '2026-10-25T00:00:00Z'}
        self.request.return_value = subprocess.CompletedProcess(
            [], 0, 'HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n' + json.dumps(existing), '')
        github_release.publish()
        self.assertEqual(self.api.call_count, 1)

    def test_rejects_existing_draft_or_unpublished_release(self):
        for existing in [
            {'tag_name': '2.0.5', 'draft': True, 'published_at': None},
            {'tag_name': '2.0.5', 'draft': False, 'published_at': None},
            {'tag_name': '2.0.4', 'draft': False, 'published_at': '2026-10-25T00:00:00Z'},
            {},
        ]:
            with self.subTest(existing=existing):
                self.api.reset_mock(side_effect=True)
                self.api.return_value = {'ref': 'refs/tags/2.0.5', 'object': {'sha': 'a' * 40}}
                self.request.return_value = subprocess.CompletedProcess(
                    [], 0, 'HTTP/2.0 200 OK\nContent-Type: application/json\n\n' + json.dumps(existing), '')
                with self.assertRaisesRegex(RuntimeError, 'unpublished or does not match'):
                    github_release.publish()
                self.assertEqual(self.api.call_count, 1)

    def test_api_failure_does_not_create_release(self):
        self.request.return_value = subprocess.CompletedProcess([], 1, 'HTTP/2.0 403 Forbidden\r\n', '')
        with self.assertRaises(RuntimeError):
            github_release.publish()
        self.assertEqual(self.api.call_count, 1)

    def test_tag_404_rejects_matching_draft_on_later_listing_page(self):
        self.api.side_effect = [
            {'ref': 'refs/tags/2.0.5', 'object': {'sha': 'a' * 40}},
            [[{'tag_name': '2.0.4', 'draft': False, 'published_at': 'earlier'}],
             [{'tag_name': '2.0.5', 'draft': True, 'published_at': None}]],
        ]
        with self.assertRaisesRegex(RuntimeError, 'unpublished'):
            github_release.publish()
        self.assertEqual(self.api.call_count, 2)

    def test_tag_404_preserves_matching_published_release_found_in_listing(self):
        self.api.side_effect = [
            {'ref': 'refs/tags/2.0.5', 'object': {'sha': 'a' * 40}},
            [[{'tag_name': '2.0.5', 'draft': False, 'published_at': 'published'}]],
        ]
        github_release.publish()
        self.assertEqual(self.api.call_count, 2)

    def test_draft_listing_failure_or_malformed_evidence_never_creates_release(self):
        for response in [subprocess.CalledProcessError(1, 'gh'), [], {}, [None], [[{}]]]:
            with self.subTest(response=response):
                self.api.reset_mock()
                self.api.side_effect = [
                    {'ref': 'refs/tags/2.0.5', 'object': {'sha': 'a' * 40}}, response,
                ]
                with self.assertRaises((RuntimeError, subprocess.CalledProcessError)):
                    github_release.publish()
                self.assertEqual(self.api.call_count, 2)

    def test_moved_or_missing_tag_does_not_create_release(self):
        self.api.side_effect = [{'ref': 'refs/tags/2.0.5', 'object': {'sha': 'f' * 40}}]
        with self.assertRaises(ValueError):
            github_release.publish()
        self.request.assert_not_called()

    def test_historical_source_requires_independent_recovery(self):
        os.environ['RELEASE_SOURCE_REF'] = 'tags/2.0.5'
        os.environ['RELEASE_SOURCE_REF_SHA'] = 'a' * 40
        with self.assertRaises(ValueError):
            github_release.publish()
        self.run.assert_not_called()
        self.api.assert_not_called()

    def test_other_branch_cannot_create_implicit_tag(self):
        os.environ['RELEASE_SOURCE_REF'] = 'heads/other'
        with self.assertRaises(ValueError):
            github_release.publish()
        self.api.assert_not_called()

    def test_prerelease_is_not_stable(self):
        (Path(self.temp.name) / 'release-history.json').write_text(json.dumps({'tag': '2.0.5-rc.1'}))
        self.api.side_effect = [
            {'ref': 'refs/tags/2.0.5-rc.1', 'object': {'sha': 'a' * 40}}, [[]], {'body': 'Notes'}, {},
        ]
        github_release.publish()
        self.assertIn('prerelease=true', self.api.call_args_list[-1].args)

    def test_workflow_retains_trusted_helper_and_gates_publication(self):
        workflow = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertEqual(workflow.count('scripts/release/github_release.py scripts/release/publisher.asc'), 2)
        history = workflow.index('      - name: Publish verified release history')
        create = workflow.index('      - name: Create GitHub release with filtered generated notes')
        self.assertLess(history, create)
        self.assertLess(workflow.index('      - name: Validate GitHub release source before publication'),
                        workflow.index('      - name: Authenticate NuGet trusted publisher'))
        self.assertIn('if: inputs.dry-run == false', workflow[create:workflow.index('      - name: Perform release dry-run')])
