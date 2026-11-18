# Architecture and ownership

## Boundary

`KeePassXC.CloudTransport` contains provider I/O, Google OAuth Desktop PKCE, Proton CLI command construction, S3 SigV4 and WebDAV HTTP. It does not contain application configuration formats, UI, vault/state schemas, conflict resolution, backup policies, LWW, or scheduling.

A consumer composes:

```
Consumer app -> CloudTransportFactory -> ICloudTransport
                          |                 |-- GoogleDriveTransport / GoogleOAuthSession
                          |                 |-- ProtonDriveTransport / ICloudCliRunner
                          |                 |-- S3Transport (also R2)
                          |                 `-- WebDavTransport
                          `-> host ICloudSecretStore / ICloudBrowserLauncher
```

The package is `net10.0` and source-compatible with NativeAOT. It uses System.Net.Http, System.Text.Json, System.Security.Cryptography, System.Xml and optionally System.Diagnostics.Process, without Windows-only APIs or UI libraries.

## Transport contract

`DownloadAsync` returns false only for a *definitively missing* remote object. All ambiguous results are exceptions. For data safety, a download destination must not already exist; the caller chooses an isolated staging path, verifies a digest and replaces its own file transactionally.

`IConditionalCloudTransport` is an optional interface implemented only by S3 and WebDAV. It enforces revision preconditions on the remote HTTP request. The consumer must treat provider capability reports as part of its safety policy and must not emulate server CAS with a separate read and write.

## Google Drive

Google Drive object names can repeat. The implementation refuses duplicates rather than selecting an arbitrary file ID. Only positive folder-ID paths may be cached, for a bounded time; file state is never cached. A new Google OAuth client may not be authorized to access Drive objects created by a different OAuth application using `drive.file` scope, even on the same Google account.

The host supplies its own OAuth public Desktop client identity, credential targets and browser launcher. The package does not embed the Cloud Sync app's OAuth client. A successful login verifies the Google account before writing the refresh token. Settings changes should create a new transport instance to avoid stale cached OAuth sessions.

## Proton Drive

Uses the official `filesystem list`, `create-folder`, `download`, `upload` and `trash` CLI commands. No private API is used. CLI installation and update UI remain owned by the consumer. A CLI use lease protects an opened binary from replacement until `Dispose()`; `TryBeginUpdate()` offers a non-blocking update gate. The executable is additionally opened with a Windows no-delete file share during a session. CLI login is initiated by the host, then verified by listing `/my-files`.

## Application integration

Cloud Sync must preserve its wire schema 2, remote paths, cloud IDs, instance IDs, immutable history and conflict handling. A thin adapter can translate the original `IRemoteStore`/`IConditionalWriteStore` API and error classification to the new transport contracts. Its INI parser converts existing provider fields to `CloudConnectionOptions` without changing files. Never change source vault data during transport migration.

Card Print may use one selected cloud and independently implement a checksummed, immutable snapshot protocol with a last-write-wins policy. That protocol does not belong in this library. Local printer names, window positions, key-file paths and other machine-specific data should not be exported by default.

## Release boundary

`ci.yml` validates any pushed or proposed code and cannot write releases or
request NuGet OIDC credentials. `release-assets.yml` builds from an existing
version tag and prepares exactly two assets on an empty Draft Release. It does
not publish the draft. `publish-nuget.yml` only transfers an already published
immutable GitHub Release package to nuget.org via Trusted Publishing and is
separately dispatched. No release workflow changes source or commits.
