# Release notes

The protected Release workflow creates a GitHub release after NuGet publication
and verified release-history publication. New package publication requires `main`;
non-main requests fail before target checkout or release preparation. Historical
preparation and dry runs remain available without package or remote Git publication.
The notes helper verifies the exact authoritative remote version tag, preserves an
existing published release, rejects drafts or unpublished releases, and marks semantic
prerelease tags as prereleases. Dry runs create no GitHub release.

## Classification

`omit-from-release-notes` excludes a PR from generated notes. Pinned `actions/labeler`
uses the support-path union in `.github/labeler.yml` and `sync-labels: true` on PR
open, update, edit, reopen, and close. The rule permits mixtures of UnitTests/,
IntegrationTests/, workflow files, labeler/release-note configuration and documentation,
README.md, signing documentation, and Python release tests. Production code, release
tools, runtime/build manifests, unknown paths, and any nonmatching path remain visible.
Documentation text qualifies even when included in the NuGet package.

Trusted safeguards check both paths of a rename, require a complete and unique file
listing, and compare PR state and file metadata before and after labeling. Failed,
skipped, or unverifiable classification attempts to remove the omission label.
These checks do not make label updates atomic or guarantee cleanup during API outages.
Runner termination can also prevent cleanup.
The workflow never checks out PR code. Other labels, titles, and authors do not hide
changes. Manual label applications can be removed by a later classification run.

This label is GitHub release-note metadata. The repository-local Jira caller skips
its exact add/remove label events while preserving other Jira events. Existing Jira
copies are not cleaned, and other synchronization paths can retain copied metadata.

## Before releasing

Publication remains asynchronous. For each relevant PR since the previous release,
run **Classify release notes** with its PR number to backfill classification, including
merged PRs. Check that the completed run used the final PR changes, inspect those
changes against the support-path policy, and verify the resulting omission label.
Resolve failed or skipped runs and incorrect labels before starting the release.
A green run alone does not prove that later changes were classified.

There is no automated release barrier. If manual verification is missed, published
notes can retain omissions or unwanted support entries. Already published notes are
preserved; automated synchronization is outside this PR and is not scheduled.

## Recovering a failed notes step

Use **Recover GitHub release notes** from `main` and enter the original release run ID
and the attempt containing its `nuget-package-ATTEMPT` artifact. The same protected
`release` environment approval applies. Recovery verifies the retained signed manifest,
reconstructs and verifies both signed history commits and the annotated tag from the
bundle in a temporary repository, and compares the exact tag object with GitHub.
It creates only a missing GitHub release and preserves an existing published release.
It never rebuilds or signs artifacts, republishes NuGet packages, or pushes Git refs.

For local recovery, authenticate `gh` as the configured `scylladb-publisher` account
and run the trusted tools with `GITHUB_REPOSITORY=scylladb/alternator-client-csharp`
and `PACKAGE_OUTPUT_DIR` pointing to the downloaded artifact directory:

```sh
python3 scripts/release/github_release.py recover
```

Recovery needs `release-history.json`, its `.asc` signature, and
`release-history.bundle`. Missing, expired, tampered, or changed authority stops
recovery. Actions artifact retention limits availability. This verifies the retained
history and tag, not the bytes currently served by NuGet. A package-publication or
Git-history-publication failure still requires the signing policy's reconciliation
procedure; notes-only recovery cannot repair those earlier stages.
