# Initial repository setup

This is a one-time source bootstrap for an initially empty repository. No
existing repository is changed by preparing this source archive.

1. Create an **empty** public repository named `KeePassXC-Cloud-Transport`
   under `gamba69`, with `main` as the default branch. Do not initialize it
   with another README or `.gitignore`; they are included here.
2. Extract the archive and place **the contents of the
   `KeePassXC-Cloud-Transport/` directory** in the new repository root.
3. Run `build.bat -NoPause` on Windows with .NET 10 SDK and Windows C++ build
   tools. The full gate includes source audit, compilation, named regression
   tests, NativeAOT smoke, NuGet packaging and checksum verification.
4. Commit the reviewed source and push to GitHub. Confirm that CI is green
   and contains exactly the expected unpublished `.nupkg` and `.sha256` files.
5. Configure GitHub release immutability, the `nuget-publish` environment and
   `NUGET_USERNAME` variable. Configure NuGet.org Trusted Publishing for the
   `publish-nuget.yml` workflow. See [Releasing](RELEASING.md).
6. Complete the real-provider acceptance tests using isolated test folders and
   synthetic objects. No real credentials are used in automatic CI.
7. Follow the two manual release workflows in [Releasing](RELEASING.md). The
   selected first release version is `1.0.1`; ordinary CI does not publish.
8. After the independent package is published and validated, migrate
   KeePassXC Cloud Sync and then Card Print using separate version-bumped
   `.patch` files. Do not modify their repositories directly.

After the initial commit, all changes to this repository are delivered as
version-bumped `.patch` files. The version is defined in `Directory.Build.props`.
Only the root `CHANGELOG.md` records historical changes; all other documents
describe the latest version and release procedure.
