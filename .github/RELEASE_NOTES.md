# Release notes

The protected Release workflow creates a GitHub release after NuGet publication
and verified release-history publication. New package publication requires `main`;
non-main requests fail before target checkout or release preparation. Historical
preparation and dry runs remain available without package or remote Git publication.
The notes helper verifies the exact authoritative remote version tag, preserves an
existing published release, rejects drafts or unpublished releases, and marks semantic
prerelease tags as prereleases. Dry runs create no GitHub release.

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
