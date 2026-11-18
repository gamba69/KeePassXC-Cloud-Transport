# Changelog

## [1.0.2] - 2026-11-18

- Fix manual Draft Release validation: GitHub's by-tag REST endpoint only resolves published releases; enumerate authenticated releases for drafts.
- Give the read-only Draft preflight procedure a release-capable token, without granting publishing permissions to ordinary CI.
- Pin release validation tooling to the workflow-dispatch commit, independently from the immutable source tag. This permits completing the existing 1.0.1 Draft Release after deploying the 1.0.2 workflow fix.
- Retain source tag/commit checks, release identity checks, required empty assets, checksum verification and manual publication boundaries.

## [1.0.1] - 2026-11-18

- Add separate manual GitHub Release asset preparation and NuGet.org publishing workflows.
- Require tagged source, single-version consistency, immutable release attestation and exact package digests before NuGet publishing.
- Use GitHub OIDC Trusted Publishing rather than storing an API key; reject duplicate NuGet.org package versions.
- Verify package manifest and downloadable release assets; keep ordinary CI read-only.
- Publish step-by-step release documentation, repository About metadata and mandatory preflight checks.

## [0.1.0] - 2026-11-17

- Establish independent .NET 10 provider transport API for Google Drive, Proton Drive, HTTPS WebDAV and S3/Cloudflare R2.
- Add host-owned credentials, Google Desktop OAuth with PKCE, Proton CLI runner and update exclusion lease.
- Add capability-disclosed conditional server writes, path validation, normalized failure classes and isolated HTTP/CLI tests.
- Add offline build scripts, Windows NativeAOT smoke, unpublished NuGet package output and read-only CI.

**Qualification:** Initial implementation snapshot. NativeAOT compilation, Windows CI and live-provider acceptance have not been executed in the authoring environment. Do not publish to NuGet or depend on it in production until these checks pass.
