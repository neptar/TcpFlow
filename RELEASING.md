# Releasing BigDog.TcpFlow

Releases are published by the `publish` job in [`.github/workflows/ci.yml`](.github/workflows/ci.yml) when a `v*` tag is pushed. That job only runs once the build and tests have passed on Linux, Windows and macOS, and the package smoke test has passed with the .NET 8 SDK.

## One-time setup

1. **nuget.org account.** Sign in at [nuget.org](https://www.nuget.org) with the account that should own the package.
2. **Reserve the `BigDog.` prefix (recommended).** Prefix reservation stops anyone else from publishing `BigDog.*` packages, and your packages get a "verified" checkmark. See [ID prefix reservation](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation) for how to apply.
3. **Trusted publishing policy.** Releases use [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the workflow swaps a GitHub-signed OIDC token for a nuget.org API key that lasts one hour, so no API key is ever created or stored. On nuget.org, click your username → *Trusted Publishing* → add a policy:

   | Field | Value |
   |---|---|
   | Repository Owner | `neptar` |
   | Repository | `TcpFlow` |
   | Workflow File | `ci.yml` (the file name only, not the `.github/workflows/` path) |
   | Environment | `nuget.org` (optional; restricts publishing to the workflow's `publish` job) |

   Allow it to push new packages as well as new versions, since the first release creates `BigDog.TcpFlow`. If the repository is private, the policy starts out active for only 7 days and becomes permanent after the first successful publish; you can restart the 7-day window from the same page.
4. **GitHub secret.** In the repository → *Settings* → *Secrets and variables* → *Actions*, add `NUGET_USER` containing your nuget.org **profile name** (not your email address). That's the only secret needed.
5. **Optional: manual approval.** Under *Settings* → *Environments* → `nuget.org` (created on the first tagged run, or create it now), add yourself as a required reviewer so each release waits for your OK.

## Each release

1. Update `VersionPrefix` in [`Directory.Build.props`](Directory.Build.props). Follow [SemVer](https://semver.org): a breaking change to the API **or the wire format** is a major version.
2. In [`AnalyzerReleases.Shipped.md`](src/BigDog.TcpFlow.Generator/AnalyzerReleases.Shipped.md), add a `## Release x.y.z` section and move the rules from `AnalyzerReleases.Unshipped.md` into it.
3. After the first release (1.0.0), set `<PackageValidationBaselineVersion>` in [`BigDog.TcpFlow.csproj`](src/BigDog.TcpFlow/BigDog.TcpFlow.csproj) to the previous version, so `dotnet pack` fails on accidental breaking API changes.
4. Commit, then tag and push:

   ```shell
   git tag v1.0.0
   git push origin v1.0.0
   ```

   The tag sets the package version (`v1.0.0` → `1.0.0`; `v1.1.0-beta.1` gives a prerelease).

Builds on `main` and pull requests produce `x.y.z-ci.N` packages as workflow artifacts, but never publish them.

## Checking a package locally

```shell
dotnet pack src/BigDog.TcpFlow -c Release -o artifacts
cd tests/PackageSmokeTest && dotnet run      # builds a consumer against the .nupkg, not the project
```

Open the `.nupkg` with [NuGet Package Explorer](https://github.com/NuGetPackageExplorer/NuGetPackageExplorer) to check the README, the license, the Source Link details and both `lib/net8.0` and `lib/net10.0` builds.
