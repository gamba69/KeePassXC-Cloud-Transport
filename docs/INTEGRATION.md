# Consumer migration guide

## Recommended rollout

1. Create a new independent repository from this source archive and complete Windows CI and real-provider acceptance.
2. Verify package ID availability and publish a reviewed immutable package to nuget.org (or an approved package feed). Do not publish the unqualified initial snapshot.
3. Patch KeePassXC Cloud Sync to reference the fixed package version. Preserve existing configuration, remote wire schema 2, secrets, worker scheduling, preflight and conflict semantics. Remove embedded provider implementation source only after regression tests pass.
4. Patch Card Print independently to reference that exact version, with its own credential targets, remote namespace and settings-level sync algorithm.

Each existing repository is modified exclusively by a version-bumped `.patch` against its then-current HEAD. No direct writes to their GitHub repositories are part of this delivery.

## Adapting the old IRemoteStore

- `WriteMode.Replace` -> `CloudWriteMode.Replace`.
- `WriteMode.CreateNew` -> `CloudWriteMode.CreateNew`.
- `RemoteFile.Path` -> `CloudObjectInfo.Path`.
- `IConditionalWriteStore.ReadTokenAsync` -> `IConditionalCloudTransport.ReadRevisionAsync`.
- `IConditionalWriteStore.UploadConditionalAsync` -> same method on optional new interface.
- `SyncSafetyException` is an application exception. Translate `CloudSafetyException` and `CloudTransportException` to existing Cloud Sync status categories without treating unknown or integrity errors as ordinary outages.
- Preserve the old full remote path by setting `RemotePrefix = ""` for Cloud Sync. The engine already supplies its own remote directory in paths; do not prefix it twice.
- Map existing INI `Provider`, `WebDavUrl`, `ProtonRootPath`, `S3Region`, `GoogleClientId`, refresh-token target and timeout to new options in the adapter, rather than changing saved INI content.
- Preserve the Cloud Sync Google Desktop OAuth client ID/secret *in that application's composition root*, not in the library. This avoids an unnecessary OAuth client change.

## Host-provided interfaces

Cloud Sync and Card Print must each implement `ICloudSecretStore`, usually via their existing Windows credential management. The host is also responsible for opening a browser and installing/updating Proton CLI. A safe default `ProcessCloudCliRunner` is available if the host has no existing CLI process runner.

## Package distribution

NuGet Package ID currently configured as `KeePassXC.CloudTransport` (working name; confirm availability before publication). The package is a normal `net10.0` library with no external packages and is compatible with self-contained NativeAOT consumers. `dist/` contains packages only after a successful local Windows build.
