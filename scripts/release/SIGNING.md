# Repository release signing

The public certificate in `publisher.asc` is safe to commit and is registered on
`scylladb-publisher`. Keep it available to consumers; it does not belong in secrets.

* Certification fingerprint: `1671E699F54C805D753B18E1E170B137B8BA59B2`
* Signing fingerprint: `D80C8C1E16883977B1A14D54885897037665FAA2`
* Identity: `ScyllaDB Publisher (scylladb/alternator-client-csharp) <publish.code@scylladb.com>`
* Expiration: `2028-10-19T15:45:41+00:00`

Protected `release` environment secrets `GPG_PRIVATE_KEY` (ASCII-armored protected
signing-subkey export) and `GPG_PASSPHRASE` (nonempty single-line passphrase, at most
1024 UTF-8 bytes) supply release signing.
Never upload the full primary private-key backup to CI. Packages and checksums use
the signing subkey, selected by its complete fingerprint with `!`. The primary key
certifies the signing subkey and remains offline.

Release Make targets and directly callable history commands enforce the local
operator policy before release work begins: the authenticated `gh` user must be
`scylladb-publisher` (use `gh auth switch` if needed). In Actions, the protected
`release` environment authorizes operators through `scylladb/drivers` approval,
including dry runs and reruns. Approved maintainers may differ from the publisher;
the actor's account does not select the signing key. The guard owns the local
operator check for Make entrypoints and directly callable guard commands.
The signer, NuGet publisher credentials, and release operator are separate identities.
A manually set `GITHUB_ACTIONS=true` does not establish deployment approval or grant
credentials.

Each signing stage runs in a scoped `signing.py exec` session. The session imports
only the protected signing subkey, checks that primary private material is absent,
and verifies a fresh-agent signing preflight against the pinned public certificate.
Subkey protection is checked again after first use to reject unprotected native exports.
The preflight agent is stopped before the wrapped command starts, so its cached
passphrase cannot authorize the command's first signature.
Anonymous pipes supply the key import and passphrase; neither credential is written
to a temporary file or included in command-line arguments. Git configuration applies
only to the command and its children. Temporary keyrings are removed on success, failure, or catchable cancellation,
without changing checkout configuration or writing state to `GITHUB_ENV`.
Scoped sessions remove private-key source assignments from Make's serialized
parent overrides so nested Make commands cannot restore those inputs. Nonsecret
command-line overrides retain their precedence, and Make execution flags remain.

For maintainer-driven release preparation:

```sh
export RELEASE_GPG_PRIVATE_KEY_FILE=/private/path/signing-private.asc
export RELEASE_GPG_PASSPHRASE_FILE=/private/path/passphrase.txt
export RELEASE_VERSION=X.Y.Z
# Optional: otherwise the next patch version is used.
export RELEASE_NEXT_VERSION=X.Y.N
make release-dry-run
```

`release-prepare` runs `release-build`, `release-history`, then `release-sign`.
Building first plans version-only Git trees without changing the checkout. It performs the
existing build, formatting, unit-test, package, and installation checks; set
`RELEASE_SKIP_TESTS=true` to skip project tests while retaining build, formatting,
packaging, and installation checks. `RELEASE_VERSION` optionally overrides the NuGet
version and release tag. `RELEASE_NEXT_VERSION` selects the next numeric version;
the development commit adds the NuGet suffix `dev`. Build metadata in release
versions, conditional project versions, and competing `Version`/`PackageVersion`
properties are unsupported. Unsupported targets fail before building or importing keys.
Package tooling receives neither release credential values/file paths nor session
Git/GPG configuration, including credentials passed through Make overrides. Build
tools also receive no `NUGET_PUBLISH_TOKEN`; only the publishing step receives the
short-lived credential returned by NuGet trusted publishing.
Each release build removes old `.nupkg`, `.nupkg.asc`, `SHA256SUMS`, and
`SHA256SUMS.asc` outputs from `PACKAGE_OUTPUT_DIR` before creating new packages.
Other files in the output directory are preserved.

`release-history` creates a signed release commit, a signed annotated tag named
exactly after the release version (for example `2.0.6`), and a signed development
commit (for example `2.0.7-dev`). Trusted Git plumbing creates these objects without
running project code or Git hooks. The original checkout and `HEAD` remain unchanged;
the prepared development commit is available at `refs/heads/release-prepared/VERSION`.
All signing still goes through the scoped signing helper and pinned subkey.

`release-history.json` records the original baseline, versioned source trees, commit
IDs, and tag. Verification independently reconstructs the expected version-only
trees from the baseline, requires exactly the two new commits with the expected
parents, verifies both signatures and the annotated tag, and checks its target.
The manifest is signed alongside the packages. `release-history.bundle` retains
the Git objects and refs for independent inspection, including dry runs and historical
releases. The signed checksums cover the bundle, manifest, and complete package set.
The packages' source revision remains the validation baseline; the manifest links
that baseline to the version-only release commit without rebuilding after approval.

`release-sign` verifies prepared history, generates `SHA256SUMS`, then signs and
verifies the packages, manifest, and checksums in one batch.
Checksum entries use the C locale's ASCII filename order,
matching publication verification regardless of the caller's locale.
Existing signatures are verified without being overwritten.
`release-dry-run` prepares and signs outputs without uploading them. `release` also
pushes packages only through the protected Actions workflow using NuGet trusted
publishing. `NuGet/login` exchanges the job's GitHub OIDC identity for a short-lived
credential immediately before publication. No stored NuGet API-key secret is used.
Even a direct `release-push` verifies prepared history, the signed manifest,
every detached package signature, the checksum signature, bundle checksums, and
the exact complete package set and version before uploading anything.
NuGet receives the temporary credential through its required CLI option inside the
helper; Make never expands or prints its value. In Actions, validation, tests,
packaging, and installation checks finish before protected approval is requested.
The validation job has no release environment, signing secrets, publishing token, or
OIDC permission. It always runs the trusted release helper tests from the workflow
revision, including when project tests are skipped. It preserves the current Makefile,
signing helper, log guard, and certificate outside the checkout before selecting an older release target. A branch
input selects its tip; a tag input selects its preceding commit, preserving the existing rerun
behavior. Existing tags remain untouched. A historical rebuild needs a new unused
release version; it must not replace the selected tag or another published tag.
Selected targets must retain the .NET 8 `ScyllaDB.Alternator` package project policy.
Always invoke the preserved Makefile with `make -f` after changing revisions; the
checkout helper no longer overwrites historical files or deletes local tags.
Validation records the exact selected commit SHA and the original branch or tag ref
and object SHA as job outputs. For `main`, the selected commit must equal the workflow
launch SHA; if `main` advanced before checkout, validation stops before building.
A dependent release job runs on a fresh runner with the protected `release`
environment. It checks out release tools at the immutable workflow revision
(`github.sha`) and downloads only the exact validated package artifact by ID from
this workflow run. It independently fetches the recorded source SHA and reconstructs
the release plan using tools preserved from the workflow revision. The release tree
must match validation's tree output. Package bytes are treated as data: this runner
never restores, builds, tests, installs, or executes selected-target code. It creates
and verifies signed history, signs the unchanged packages, rechecks release blockers,
then conditionally publishes. Dry runs still require approval and create signatures
and local Git refs, but skip NuGet login, upload, and remote Git pushes.
Before importing signing credentials, the release job checks the recorded source
ref through GitHub's API. A changed or missing branch or tag stops the run, including
dry runs, and requires new validation. Publication repeats this check before
requesting a NuGet token. These checks compare the original ref without selecting
new source or rebuilding packages. Historical tag targets retain their recorded
preceding commit, and moving the tag cannot switch the approved package bytes.
Artifact names include the run attempt; release-only reruns reuse the validation
output ID and pass through the protected environment again. Local preparation still builds
and signs on the same machine; its scopes prevent direct credential inheritance,
but do not protect against arbitrary same-user persistence.

After successful NuGet publication, a non-dry-run release targeting `main` pushes
the verified development commit to `refs/heads/main` and the signed tag together.
The push uses an explicit destination, an atomic ref update, and a lease requiring
remote `main` to equal the original baseline. A concurrently advanced or rewritten
branch, existing tag, or rejected ref leaves both remote refs unchanged. Git pushes
disable hooks. Historical targets retain history in the artifact without pushing
to `main`. Local preparation leaves refs local; `release-push-history` is a separate,
explicit operation. The workflow's Git token has write permission only in the
protected release job; validation remains read-only.

Complex pre-release checks can run before preparation or against signed outputs
after the session closes. Verification needs only the public certificate. Checks
that need to create additional signatures can use their own scoped invocation:

```sh
python3 scripts/release/signing.py exec make YOUR_CHECK_TARGET
```

The low-level signing helper does not impose release-account policy. Wrapped
commands have no overall time limit; each GPG operation has its own timeout and
reads the current passphrase, so long checks do not depend on an agent cache.
A failed command propagates its exit status and still cleans up the session.
Helper failures report a fixed operation label and exit status or error category,
without including command arguments, captured output, or exception payloads.
Do not change or replace existing release tags. A failed verification blocks publication.
Repeated preparation of an existing version fails before rebuilding; resume a
completed local preparation with verification/signing commands, or select a new version.

Consumers should obtain this public certificate from this repository at a reviewed
commit, check the certification fingerprint above through a trusted channel, and
verify release signatures. The Ubuntu keyserver can also serve the certificate:

```sh
gpg --keyserver hkps://keyserver.ubuntu.com --recv-keys 1671E699F54C805D753B18E1E170B137B8BA59B2
gpg --fingerprint 1671E699F54C805D753B18E1E170B137B8BA59B2
gpg --status-fd 1 --verify ARTIFACT.asc ARTIFACT
```

Require a successful GnuPG exit status, exactly one `GOODSIG` matching the signing
subkey's long key ID, and exactly one `VALIDSIG` with the complete signing and
primary fingerprints above. Reject bad, ambiguous, expired, revoked, or failed
signature statuses even when a matching `VALIDSIG` is also present. The helper
enforces these checks for package signatures, Git objects, and its signing preflight.
`git verify-tag --raw TAG` and `git verify-commit --raw COMMIT`
provide the equivalent check for Git objects. Keyserver presence alone establishes
neither trust nor the expected repository identity.

Rotate by preparing a new independent certificate, validating its protected subkey,
registering its public certificate on the publisher account, publishing the public
certificate, reviewing fingerprint/public-certificate updates together, and updating
the secret pair in a coordinated release pause. Keep old public certificates available
for historical releases. Never revoke the previous key merely to rotate it. If a key
is compromised, stop releases and use the offline revocation certificate; fix the
exposure before resuming. After partial publication, inspect the release log and
NuGet's package-version list to identify accepted versions. Verify the retained
packages and signatures before retrying. The existing
[`--skip-duplicate`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push#options)
push treats version conflicts as warnings and continues with other packages.
NuGet publication and Git publication cannot form one transaction. If NuGet succeeded
but the final Git push failed, preserve the retained signed package and history
artifacts, inspect both destinations, and reconcile the verified history before
starting a new release. Do not force a tag, rewrite the advanced branch, or assume
a rebuilt duplicate package is the originally published package.

## NuGet signatures

The workflow signs the exact `.nupkg` bytes and retains packages, `SHA256SUMS`, the
signed history manifest, Git bundle, and their signatures in the
`nuget-package-RUN_ATTEMPT` Actions artifact. OpenPGP
signatures are detached and are not embedded in NuGet's native signature format. Assembly strong-name signing remains
separate. Git release commits and tags use the same repository signing subkey.

These detached signatures verify the retained pre-publication package bytes.
[NuGet.org repository signing](https://devblogs.microsoft.com/dotnet/introducing-repository-signatures/)
changes the package bytes, so a package downloaded from nuget.org cannot be verified
with this detached signature. Download the matching package, checksums and signatures
from the Actions artifact while it remains available. Actions artifacts expire under
the repository's retention policy; this workflow provides no permanent package or
signature archive. On a `--skip-duplicate` rerun, the artifact may contain a rebuilt
package that NuGet did not publish because the version already existed. Its signature
authenticates those retained bytes, without proving they were published to NuGet.

## Sanitized release logs

`scripts/release/guard.py` captures build and publication stdout/stderr in bounded
memory (8 MiB per command), then redacts complete streams before printing or writing
`stdout.log` and `stderr.log`. It covers raw, multiline, JSON/XML, URL, and Base64
forms of supplied secrets, including signing input files. Raw output is never
spooled to disk. Overflow, incomplete streams, and sanitization failures emit fixed
diagnostics. Log write failures preserve the command status. The workflow uploads
only sanitized logs on success or failure. Set `RELEASE_LOG_DIR` locally to retain
logs elsewhere (default `/tmp/alternator-release-logs`).
Log directories and files are created with modes `0700` and `0600`. Existing log
paths must be owned by the current user and private; symlinks, hard-linked files,
and shared or foreign-owned parent directories are rejected (root-owned sticky
parents such as `/tmp` are allowed). Already-private logs retain append behavior.
For an older shared log directory, choose a new private `RELEASE_LOG_DIR`; rejected
paths emit the fixed log-write diagnostic without changing the child command status.

Release controls reject shell syntax, credential-bearing source URLs, and debug
verbosity before launching the build. The guard is a credential/log safety boundary,
not a sandbox for untrusted project files. Cancellation stops owned process groups;
uncatchable kills or machine failure still require runner/workspace cleanup.

## Deployment environment rollout

The GitHub [`release` environment](https://github.com/scylladb/alternator-client-csharp/settings/environments)
was provisioned on 2026-10-22 with required approval from `scylladb/drivers`, self-review
disabled, administrator bypass disabled, and a custom branch rule permitting only
`main`. The `drivers` team has explicit read access to this repository so GitHub can
use it as an environment reviewer.
The branch rule applies to the workflow launch ref, including historical target runs.
Only the dependent release job declares this environment, so approval follows
successful validation and covers signing and NuGet publication on a separate runner.
YAML alone does not configure these protections or provision secrets. A runtime
preflight checks that `scylladb/drivers` is the sole required reviewer, self-review
and bypass are disabled, and the launch-ref rule allows only `main`. It runs before
target checkout and again on the release runner; it fails closed if the environment
is missing, has a different or additional reviewer, or is otherwise configured
differently.

Validate the protected signing-subkey export and nonempty single-line passphrase,
then upload `GPG_PRIVATE_KEY` and `GPG_PASSPHRASE` to that environment
through stdin or a secret-management API. Do not place values in shell command
arguments or logs. Test an approved upload-free dry run launched from `main`, including
initial dispatch and rerun by approved maintainers who differ from `scylladb-publisher`,
then remove repository-level copies of the signing secrets after the environment-aware
workflow is active.
The environment contains `GPG_PRIVATE_KEY` and `GPG_PASSPHRASE`, uploaded through
stdin from the validated repository-specific signing-subkey export and passphrase.
An approved workflow dry run and repository-secret retirement remain rollout steps.
Configure a NuGet.org trusted publishing policy for owner `scylladb`, repository
`alternator-client-csharp`, workflow file `release.yml`, and environment `release`,
scoped to `ScyllaDB.Alternator`, under the NuGet.org account `scylladb.publish.code`.
The workflow sets this public account name directly; no username variable is needed.
The job requests `id-token: write`; the pinned `NuGet/login` action runs only on
publishing runs, after package verification and the release-blocker recheck. Its
masked output is passed as `NUGET_PUBLISH_TOKEN` only to `release-push`, then scrubbed
from package tooling. A missing token fails closed; stored API keys are not a fallback.
Dry runs skip token exchange and upload. Local signing/dry runs remain supported;
local NuGet publication is disabled.

See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
for policy registration and the short-lived credential exchange.

## Verification coverage

`make test-release-signing` runs disposable-key/Git and real Make tests with a .NET
substitute, plus guard subprocess tests. It covers independent Actions operator
and signer identities on initial runs and reruns, local publisher authorization,
the sole drivers reviewer, validation/approval wiring,
separate-workspace signing of unchanged package data with trusted tools,
unsigned/tampered/incompletely listed publication inputs, pinned signer and revocation
and expiry checks, matching `GOODSIG`/`VALIDSIG` records, post-use protection checks,
cleanup failures, cancellation, and sanitized output. Source checks use temporary Git
repositories and a bare remote, substituting GitHub's ref API to cover branch
advancement, moved historical tags, invalid metadata, and dispatch-SHA consistency.
The `Release signing` PR CI workflow includes both signing and guard tests.
Signed-history tests verify both commits and the tag, reject unsigned development
commits and lightweight tags, and exercise the actual atomic push against temporary
bare remotes from a detached checkout without an upstream. They cover advanced
remote branches, existing remote tags, and disabled Git hooks.

A full C# rehearsal uses the real .NET SDK to pack and install the package and then
signs/verifies its bytes and prepared history with disposable keys via
`make release-dry-run`. An approved protected-environment
workflow run remains a separate rollout check.
