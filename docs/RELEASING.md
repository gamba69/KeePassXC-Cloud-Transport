# Releasing KeePassXC Cloud Transport

This project uses **manual, separately authorized releases**. Pushing commits,
opening pull requests and pushing tags do **not** publish GitHub Releases or
NuGet.org packages. The build and GitHub release must use the same fixed source
commit and package version. Do not re-use a released version.

## Release components

| File | Trigger | Permission | Result |
| --- | --- | --- | --- |
| `.github/workflows/ci.yml` | Branch push, PR, or manual | `contents: read` | Full build/test/AOT gate; unpublished CI artifact |
| `.github/workflows/release-assets.yml` | Manual `workflow_dispatch` | `contents: read` build, `contents: write` attachment | Rebuilds and attaches verified files to a **pre-existing Draft Release** |
| `.github/workflows/publish-nuget.yml` | Separate manual `workflow_dispatch` | `contents: read` and `id-token: write` | Re-verifies an **immutable published** GitHub Release and transfers its exact package to NuGet.org |

Exactly two files are attached to each GitHub Release:

```text
KeePassXC.CloudTransport.<VERSION>.nupkg
KeePassXC.CloudTransport.<VERSION>.nupkg.sha256
```

Release workflows never commit, tag, create releases, edit release notes,
modify existing assets or skip failing tests. They never use `--clobber` or
`--skip-duplicate` to hide a duplicate.

## One-time GitHub settings

1. Create `gamba69/KeePassXC-Cloud-Transport` with `main` as its default branch,
   and push the reviewed source files.
2. In **Settings > General > Releases**, enable **Release immutability**.
   This setting must be enabled **before** publishing a Draft Release. Immutable
   GitHub Releases automatically receive GitHub-signed attestations.
3. In **Settings > Environments**, create `nuget-publish`. If available on your
   plan, require approval and limit deployments to `main`. The publishing
   workflow is deliberately dispatched from `main` but checks out the tag to
   verify its source; do not dispatch it from a tag.
4. In **Settings > Secrets and variables > Actions > Variables**, add
   `NUGET_USERNAME`, equal to the NuGet.org **profile username**, not an email
   address. This is not a secret. Do not add a NuGet API key to repository
   secrets.
5. Review **Actions > General > Workflow permissions**. The CI workflow only
   reads. Release asset upload requests `contents: write` for that job; the
   NuGet job requests `id-token: write` but not repository write access.

## One-time NuGet.org Trusted Publishing policy

After signing in to https://www.nuget.org/ and selecting your account's
**Trusted Publishing** page, configure a GitHub Actions policy:

| Policy setting | Value |
| --- | --- |
| Repository owner | `gamba69` |
| Repository name | `KeePassXC-Cloud-Transport` |
| Workflow filename | `publish-nuget.yml` (filename only, not `.github/workflows/`) |
| GitHub environment | `nuget-publish` |
| Package scope | Exactly `KeePassXC.CloudTransport` (allow creating the package on the first publish, and publishing subsequent versions) |
| NuGet.org policy owner | The account that should own the package |

NuGet.org may treat a newly created policy as pending or temporarily active
until its first successful use. Check the status on the NuGet.org Trusted
Publishing page before triggering publication. A package ID must also be
available and permitted by any reserved-ID-prefix policy. A NuGet.org policy
and GitHub environment cannot be created by the source code or by this build.

NuGet publishes through the official `NuGet/login@v1` GitHub Action. It
exchanges a GitHub OIDC token for a short-lived API key **only at publish time**;
there are no persistent API-key secrets in GitHub Actions.

## Each release — step by step

1. Set the new exact version in `Directory.Build.props` and add the release's
   section to `CHANGELOG.md` (the only release history). Ensure all tests and
   live-provider acceptance runs pass. The stable first planned release is
   `1.0.1`, as explicitly selected for this project.
2. Push the reviewed commit to `main`, ensure CI is green, and verify locally:

   ```bat
   build.bat -NoPause
   ```

3. Create and push a tag whose **entire name is the version**, without `v`:

   ```sh
   git checkout main
   git pull --ff-only
   git tag -a 1.0.1 -m "KeePassXC Cloud Transport 1.0.1"
   git push origin 1.0.1
   ```

4. Create a **Draft** GitHub Release for that **existing** tag. Title:
   `KeePassXC Cloud Transport 1.0.1`. Enter release notes; **do not attach any
   assets manually**. Do not publish yet. GitHub can create a Draft Release
   using Releases > Draft a new release.
5. Select **Actions > Release / attach verified NuGet assets > Run workflow**.
   Choose branch `main` and enter `1.0.1` in `version`. The workflow validates
   the tag, version, changelog and Draft Release identity. It runs the entire
   .NET test and NativeAOT gate on the **tagged commit**, verifies the nupkg,
   then uploads and downloads back the two assets for checksum validation.
   This step cannot modify an existing asset; if it partially fails, inspect
   the draft, remove the incomplete assets manually, and retry.
6. Inspect the two files and logs. **Publish the Draft Release manually.** With
   immutability enabled, GitHub fixes the tag/assets and produces its release
   attestation. Confirm it by running:

   ```sh
   gh release verify 1.0.1 -R gamba69/KeePassXC-Cloud-Transport
   ```

7. Select **Actions > NuGet.org / publish verified immutable release > Run
   workflow**. Choose branch `main`; enter `version=1.0.1` and
   `confirmation=PUBLISH`. If an environment reviewer was configured, approve
   the protected `nuget-publish` job. The workflow will:

   - Confirm the version, exact Git tag, published release and signed GitHub
     release attestation.
   - Download the exact two Release assets (without rebuilding).
   - Check the SHA-256, package ID, version and `net10.0` DLL.
   - Verify **both** downloaded files against GitHub release attestations.
   - Check NuGet.org for an existing version and **refuse duplicates**.
   - Obtain a one-hour key via NuGet Trusted Publishing and upload exactly
     that verified `.nupkg`.

8. Inspect the resulting package at
   https://www.nuget.org/packages/KeePassXC.CloudTransport . The public NuGet
   feed/search can be delayed after NuGet.org accepts the upload; do not
   retry publication just because search indexing is not immediate.

## Expected failure behaviour

- No tag, wrong version, mismatched changelog or moved tag: **FAIL**.
- No Draft Release, already published, draft with existing assets: **FAIL**.
- Build/test/NativeAOT/manifest/hash failure: **FAIL**, nothing published.
- Missing release attestation, mutable release or mismatched asset: **FAIL**
  before NuGet authentication.
- Missing `NUGET_USERNAME`, OIDC policy or package creation permission: **FAIL**.
- Existing NuGet version, unrecognized NuGet status or unavailable NuGet.org:
  **FAIL**. Never use `--skip-duplicate` as a workaround.

If NuGet accepted the upload but a later step fails or indexing is delayed,
check the NuGet package page and feed before a retry. Once published, the same
version cannot be replaced. Any correction must use a **new version**, following
this full release process.

## Commands and local checks

```powershell
# A normal local build (does not publish)
.\build.bat -NoPause

# Optional: verify two files in dist/ and their NuGet identity (PowerShell 7)
pwsh -File .\scripts\verify-package.ps1 -Version 1.0.1 -Directory dist
```

The scripts that read GitHub Release metadata use `gh` and a GitHub Actions
`GITHUB_TOKEN`; they are intended to execute inside their workflows. The
repository release process has no stored signing key. GitHub issues immutable
release attestations, and NuGet.org validates an OIDC identity at package
publication. These mechanisms serve different purposes.
