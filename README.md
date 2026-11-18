# KeePassXC Cloud Transport

**Reusable .NET 10 cloud-object transports for Google Drive, Proton Drive, S3/Cloudflare R2 and HTTPS WebDAV.**

[![Windows .NET 10 / NativeAOT / NuGet](https://github.com/gamba69/KeePassXC-Cloud-Transport/actions/workflows/ci.yml/badge.svg)](https://github.com/gamba69/KeePassXC-Cloud-Transport/actions/workflows/ci.yml) ![License: MIT](https://img.shields.io/badge/License-MIT-blue)

**Unofficial independent project. Not affiliated with, endorsed by, or maintained by the KeePassXC project.**

This library abstracts cloud object I/O. It deliberately does **not** synchronize KeePassXC databases, settings, or application documents: consumers own scheduling, manifests, conflict policy, integrity validation, rollback and backup retention.

> **Development status.** The original provider implementation was extracted from KeePassXC Cloud Sync. Automated CI, a clean NativeAOT build and real-provider integration checks are required before production use. The GitHub source archive is not a published NuGet package.

## Implemented providers

| Provider | Authentication | Conditional server write | Notes |
| --- | --- | --- | --- |
| Google Drive | Desktop OAuth 2.0 + PKCE | No | Google file names are not unique; duplicate names are errors. `drive.file` scope is tied to the chosen OAuth client. |
| Proton Drive | Official Proton CLI | No | Does not bundle CLI. A successful login is verified by a read-only CLI listing. |
| S3 | Signature V4 | Yes, *if* server honors `If-Match`/`If-None-Match` | Path-style HTTPS account/bucket endpoint. |
| Cloudflare R2 | Signature V4, region `auto` | Yes, *if* endpoint honors conditions | Official `*.r2.cloudflarestorage.com` endpoint. |
| WebDAV | HTTPS Basic credentials | Yes, *if* strong ETags and conditional PUT are supported | Strong `ETag` required for compare-and-swap. |

Capabilities describe the requested server-side mechanism; real server compatibility must be checked against the chosen endpoint. Google and Proton `CreateNew` do NOT provide atomic no-overwrite guarantees. Do not treat a preflight HEAD or listing followed by upload as compare-and-swap.

## Prerequisites

- .NET 10 SDK to build, plus Windows x64 C++ build tools for NativeAOT.
- Runtime: .NET 10 host or a self-contained NativeAOT application.
- Google OAuth Desktop client and host-provided safe credential storage, for Google.
- Official Proton Drive CLI, installed and authenticated separately, for Proton.
- No Avalonia, KeePassXC or Windows API dependency in the core transport package.

## Creating a connection

```csharp
using KeePassXC.CloudTransport;

// The host implements ICloudSecretStore, for example through Windows Credential Manager.
ICloudSecretStore secrets = /* host-supplied secret store */;

var options = new CloudConnectionOptions
{
    Provider = CloudProvider.WebDav,
    Endpoint = "https://example.invalid/remote.php/dav/files/account/",
    RemotePrefix = "cardprint/settings",
    CredentialTarget = "CardPrint/WebDAV/account"
};

using ICloudTransport transport = CloudTransportFactory.Create(options,
    new CloudTransportServices { Secrets = secrets });

if (!await transport.CheckConnectionAsync(CancellationToken.None))
    throw new InvalidOperationException("Login is required");

await transport.EnsureDirectoryAsync("backups", CancellationToken.None);
await transport.UploadAsync("local-backup.bin", "backups/backup.bin",
    CloudWriteMode.CreateNew, CancellationToken.None);
```

All paths are **relative to the selected remote prefix**, use `/`, and cannot contain `.` or `..` segments. Each application must select its own remote prefix and credential-store target. `CloudPath` rejects absolute paths, backslashes and other unsafe segments. Never put passwords in `CloudConnectionOptions`.

For Google configure `GoogleClientId`, optional publicly embedded Desktop client secret, and `GoogleRefreshTokenTarget`; pass a host browser launcher. Google refresh tokens are owned by the host's `ICloudSecretStore`. For Proton configure `ProtonCliExecutable` and a host login launcher; a safe non-shell CLI runner is included as a default. A host may inject a different `ICloudCliRunner`.

## Conditional writes

For cloud APIs with known conditional writes:

```csharp
if (transport is IConditionalCloudTransport conditional)
{
    string? token = await conditional.ReadRevisionAsync("settings.json", ct);
    await conditional.UploadConditionalAsync(localFile, "settings.json", token, ct);
}
```

A `null` token means the object was definitively absent at the time of inspection. The conditional upload requests an atomic `If-None-Match: *` create. A non-null strong revision token requests `If-Match`. Providers incapable of making these server guarantees do not implement `IConditionalCloudTransport`.

**Important:** Remote `DownloadAsync` returns `false` only when absence is definite. Access denied, timeout, rate limit and unknown outcomes throw. Download writes a **new** local file; the calling application must choose a staging pathname and independently verify checksum before atomically replacing its settings or vault.

## Building

```bat
build.bat
build.bat -NoPause
build.bat -NoPause -ValidateOnly
```

The default build performs mandatory source validation, .NET restore, compilation, named tests, Windows NativeAOT smoke and `dotnet pack`. Output: `dist/KeePassXC.CloudTransport.1.0.2.nupkg` and a matching SHA-256 sidecar. This is not automatically uploaded to nuget.org. The `-SkipAot` developer switch is available for intermediate local experiments; such a build is **not** qualified for release.

The source package requires **no third-party NuGet dependencies**. GitHub Actions runs the same build on `windows-latest`, and saves an unpublished package for each successful CI run. Neither pushes nor Git tags publish a release automatically. See [Releasing](docs/RELEASING.md) for the separate manual GitHub Release and NuGet.org Trusted Publishing workflows (no stored NuGet API key).

## Source layout

```text
src/KeePassXC.CloudTransport/          Library interfaces and provider implementations
tests/KeePassXC.CloudTransport.Tests/  Isolated named tests; no live credentials
examples/KeePassXC.CloudTransport.Example/ NativeAOT link smoke
scripts/build.ps1            Full Windows build pipeline
scripts/release-check.ps1    Tag, draft/published release and attestation checks
scripts/verify-package.ps1   NuGet metadata and SHA-256 validation
docs/                       Architecture, security, testing, migration notes
```

All project documentation and source code comments are English. Change history is maintained exclusively in [CHANGELOG.md](CHANGELOG.md). See [Architecture](docs/ARCHITECTURE.md), [Security](docs/SECURITY.md), [Testing](docs/TESTING.md), [Integration](docs/INTEGRATION.md) and [initial repository setup](docs/INITIAL_SETUP.md), [GitHub About](docs/GITHUB_ABOUT.md), and [release procedure](docs/RELEASING.md).
