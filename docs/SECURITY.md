# Security model

1. The library never receives or decrypts KeePassXC master passwords or KDBX entry secrets. It transports opaque bytes provided by its callers.
2. User credentials and OAuth refresh tokens are fetched from and saved to the host-owned `ICloudSecretStore`. Different consumers should use separate namespaced targets.
3. No HTTP redirects are followed with credential headers; endpoints are HTTPS-only. Google API endpoints are fixed.
4. S3 signs requests with AWS SigV4 over HTTPS. The secret key is not serialized into the options model or logs.
5. Google uses OAuth PKCE, loopback only on `127.0.0.1`, a random state and a bounded callback request. The callback page contains no OAuth code or token.
6. Proton runs only a separately installed official CLI, without shell expansion. Its account session belongs to the CLI. A successful process exit is not sufficient to prove login; the read-only root listing is authoritative.
7. A path may not be absolute, contain dot navigation, unescaped separators, control characters or URL query fragments. The CloudTransport prefix is appended once by the provider.
8. Server-side CAS is used only where available. On non-CAS providers a caller must not rely on `CreateNew` or ordinary `Replace` for concurrency safety.
9. Untrusted remote responses and CLI output are parsed with limits. Applications must impose their own maximum object sizes, digest checks, backups and rollback protection.
10. No consumer should automatically repeat an ambiguous upload merely because the connection dropped; first re-read and reconcile remote state.

**Known limitations:** Third-party S3-compatible and WebDAV servers differ in their conditional-write guarantees. Local machine compromise, credential-store compromise and malicious cloud responses cannot be eliminated by this package. Google `drive.file` scope is not a general read-everything permission. Proton CLI error messages are not a stable machine-readable protocol; they are classified conservatively. Independent real-provider acceptance is mandatory.

Do not send KDBX vaults, key files, OAuth tokens, S3 keys or private CLI session files with bug reports.

## Package release and provenance

Ordinary CI is strictly read-only and produces an unpublished package artifact.
A manually dispatched workflow with narrowly scoped `contents: write` can only
attach verified `.nupkg` and SHA-256 assets to an existing, empty Draft Release.
The user must publish that draft manually. Enable GitHub immutable releases
before doing so: GitHub then fixes the tag and attached asset identities and
issues a release attestation.

A second manually dispatched workflow checks the published release, tag source,
asset inventory, SHA-256, package manifest and GitHub release/asset
attestations **before** contacting NuGet.org. A failed attestation blocks
publication. The only publishing credential is an ephemeral NuGet.org token
obtained through GitHub OIDC (`NuGet/login@v1`, `id-token: write`). The workflow
does not store a long-lived NuGet API key and refuses existing package versions.
The GitHub `nuget-publish` environment should be protected with required
reviewers when the account plan supports them.
