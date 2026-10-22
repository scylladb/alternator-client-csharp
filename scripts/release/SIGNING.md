# Repository release signing

The public certificate in `publisher.asc` is safe to commit and is registered on
`scylladb-publisher`. Keep it available to consumers; it does not belong in secrets.

* Certification fingerprint: `1671E699F54C805D753B18E1E170B137B8BA59B2`
* Signing fingerprint: `D80C8C1E16883977B1A14D54885897037665FAA2`
* Identity: `ScyllaDB Publisher (scylladb/alternator-client-csharp) <publish.code@scylladb.com>`
* Expiration: `2028-10-19T15:45:41+00:00`

Protected `release` environment secrets `GPG_PRIVATE_KEY` (ASCII-armored protected
signing-subkey export) and `GPG_PASSPHRASE` (single-line passphrase) supply release signing.
Never upload the full primary private-key backup to CI. Packages and checksums use
the signing subkey, selected by its complete fingerprint with `!`. The primary key
certifies the signing subkey and remains offline.

The `release-prepare`, `release-build`, `release-sign`, `release-push`, `release`,
`release-dry-run`, `release-dry-run-summary`, and `checkout-one-commit-before` Make
targets enforce the local operator policy before release work begins: the authenticated
`gh` user must be `scylladb-publisher` (use `gh auth switch` if needed). In Actions,
the protected `release` deployment environment authorizes operators and reruns;
approved maintainers need not match the publisher account. The signer, NuGet
publisher credentials, and release operator are separate identities. A manually set
`GITHUB_ACTIONS=true` does not establish deployment approval or grant credentials.

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
make release-dry-run
```

`release-prepare` runs `release-build`, then `release-sign`. Building performs the
existing build, formatting, unit-test, package, and installation checks; set
`RELEASE_SKIP_TESTS=true` to skip build/format/test checks while retaining packaging
and installation checks. `RELEASE_VERSION` optionally overrides the NuGet version.
Package tooling receives neither release credential values/file paths nor session
Git/GPG configuration, including credentials passed through Make overrides. Build
tools also receive no `NUGET_PUBLISH_TOKEN`; only the publishing step receives the
short-lived credential returned by NuGet trusted publishing.
Each release build removes old `.nupkg`, `.nupkg.asc`, `SHA256SUMS`, and
`SHA256SUMS.asc` outputs from `PACKAGE_OUTPUT_DIR` before creating new packages.
Other files in the output directory are preserved.

`release-sign` generates `SHA256SUMS`, then signs and verifies the packages and
checksums in one batch. Existing signatures are verified without being overwritten.
`release-dry-run` prepares and signs outputs without uploading them. `release` also
pushes packages only through the protected Actions workflow using NuGet trusted
publishing. `NuGet/login` exchanges the job's GitHub OIDC identity for a short-lived
credential immediately before publication. No stored NuGet API-key secret is used.
Even a direct `release-push` verifies every detached package signature,
the checksum signature, and the exact complete package set before uploading anything.
NuGet receives the temporary credential through its required CLI option inside the
helper; Make never expands or prints its value. In Actions, building and signing run
in separate steps; only signing receives GPG secrets.
The workflow preserves the current Makefile, signing helper, log guard, and certificate
outside the checkout before selecting an older release target. A branch input selects
its tip; a tag input selects its preceding commit, preserving the existing rerun
behavior. Tags remain untouched, and this workflow never pushes prepared Git history.
Selected targets must retain the .NET 8 `ScyllaDB.Alternator` package project policy.
Always invoke the preserved Makefile with `make -f` after changing revisions; the
checkout helper no longer overwrites historical files or deletes local tags.
These scopes prevent direct credential inheritance; they do not provide separate
runners or protection against arbitrary same-user persistence.

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

Consumers should obtain this public certificate from this repository at a reviewed
commit, check the certification fingerprint above through a trusted channel, and
verify release signatures. The Ubuntu keyserver can also serve the certificate:

```sh
gpg --keyserver hkps://keyserver.ubuntu.com --recv-keys 1671E699F54C805D753B18E1E170B137B8BA59B2
gpg --fingerprint 1671E699F54C805D753B18E1E170B137B8BA59B2
gpg --status-fd 1 --verify ARTIFACT.asc ARTIFACT
```

Require exactly one matching `GOODSIG` and `VALIDSIG` pair with the signing
fingerprint above and the expected primary fingerprint, and a successful GnuPG exit
status. Reject expired or revoked keys/signatures, bad signatures, and error statuses
even when a `VALIDSIG` is also present. The helper enforces these checks.
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

## NuGet signatures

The workflow signs the exact `.nupkg` bytes and retains packages, `SHA256SUMS`, and
their `.asc` signatures in the `nuget-package` Actions artifact. OpenPGP signatures are detached and are not
embedded in NuGet's native signature format. Assembly strong-name signing remains
separate. This workflow does not create Git release commits or tags.

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
The publishing job declares this environment, covering signing and NuGet publication.
YAML alone does not configure these protections or provision secrets. A runtime
preflight checks approval, bypass, and branch rules before target checkout or signing;
it fails closed if the environment is missing or configured differently.

Validate the protected signing-subkey export and nonempty single-line passphrase,
then upload `GPG_PRIVATE_KEY` and `GPG_PASSPHRASE` to that environment
through stdin or a secret-management API. Do not place values in shell command
arguments or logs. Test an approved upload-free dry run launched from `main`, including
a rerun by a different approved maintainer, then remove repository-level copies of
the signing secrets after the environment-aware workflow is active.
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
substitute, plus guard subprocess tests. It covers separate Actions operators,
unsigned/tampered/incompletely listed publication inputs, strict signature status,
post-use protection checks, cleanup failures, cancellation, and sanitized output.
The `Release signing` PR CI workflow includes both signing and guard tests.

A full C# rehearsal uses the real .NET SDK to pack and install the package and then
signs/verifies its bytes with disposable keys via `make release-dry-run`. This workflow
creates no Git release commits, tags, or cloned build. An approved protected-environment
workflow run remains a separate rollout check.
