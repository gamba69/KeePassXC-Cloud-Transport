# Testing

`build.bat` requires the Windows .NET 10 SDK and NativeAOT toolchain. It fails fast on missing files, missing compilation, named test failures, missing NativeAOT executable or failed package generation. Source validation does not substitute for execution.

The repository includes isolated tests with injected `HttpMessageHandler` and a simulated Proton CLI runner. These tests do not call live clouds and must not use customer KDBX data. They cover:

- Relative path normalization and traversal rejection.
- WebDAV read-only probe, definitive 404, ETag and conditional PUT, multistatus listing.
- S3 bucket/region validation, SigV4 header presence, listing, ETag and conditional PUT.
- Google refresh token, account identity verification, token caching and absent-file behavior.
- Proton read-only session probe, login verification, prefix handling, unauthorized path and lack of CAS.
- Error classification and unexpected operation rejection.

**Not covered by isolated HTTP doubles:** Actual vendor OAuth login, real CLI login and update lifecycle, byte-for-byte S3 signing acceptance, cloud throttling and eventual consistency, concurrent writers on servers that ignore conditional headers, Windows Credential Manager and multi-device workflow. Run live-provider diagnostics in an isolated random namespace before production use.

## Acceptance matrix

| Provider | Required live checks |
| --- | --- |
| Google | Full browser OAuth, restart/token refresh, manual re-login without app restart, duplicate filenames, basic uploads |
| Proton | CLI not installed, install, authenticated and expired session, verification right after login, concurrent update guard |
| S3 | Real AWS SigV4 against a test bucket, UTF-8 keys, conditional create, conditional update and contention |
| R2 | R2 region `auto`, account endpoint validation, conditional requests, readback |
| WebDAV | Different server implementations, strong/weak ETag, 409/412, PROPFIND parsing, HEAD and PUT |

Cross-consumer acceptance additionally requires regression tests of original Cloud Sync protocol schema 2 and Card Print's settings snapshot restore semantics. It cannot be inferred from a successful cloud-connection probe.

The first release is **not** production qualified until Windows CI, clean NativeAOT packaging and every real-provider acceptance group pass. Tests must print one named `[PASS]`/`[FAIL]` per case; failures must return nonzero.

## Release automation checks

The normal `build.bat` validates the current changelog entry, fixed NuGet
identity, presence of the three GitHub Actions workflows and separation of
read-only CI from the two manual release workflows. It also verifies the
complete `.nupkg` payload and generated SHA-256 sidecar.

The release-assets workflow rebuilds and retests at the *exact tagged commit*,
not at a mutable branch head. The publish-nuget workflow downloads and checks
the published GitHub Release assets, including GitHub's immutable-release
attestations. It never rebuilds packages. Live provider tests require separate
explicit manual acceptance and are not part of CI.

Release GitHub/NuGet permissions and publishing credentials cannot be fully
tested from a local build. Before the first publication, follow
[Releasing](RELEASING.md) and inspect both manual workflows.
