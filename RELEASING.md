# Releasing

Pushing a `v*` tag runs `.github/workflows/release.yml`. It checks that the tagged commit is on `main`, that the tag is `v` + the package version, that `CHANGELOG.md` has a dated section for that version, that `PublicAPI.Unshipped.txt` is empty, and that `Directory.Packages.props` does not pin `AgentCat.Api` 0.0.4. It then runs `ci.yml` on every OS, pushes the package and its symbols to nuget.org with trusted publishing, and creates a GitHub release whose notes are the `CHANGELOG.md` section (a prerelease when the version has a suffix). When any job before the push fails, nothing is published.

## First release (one time, in this order)

1. Make the repository public.
2. Set up the gates:
   - The GitHub environment `release` (Settings, Environments) with a required reviewer, deployment branches and tags "Selected branches and tags" with the tag rule `v*`, and the environment secret `NUGET_USER`, the nuget.org profile name (not the email) of the account that owns `AgentCat`.
   - A tag ruleset for `v*` that lets only admins create, move or delete those tags.
   - On nuget.org, a Trusted Publishing policy with Repository Owner `agentcathq`, Repository `agentcat-csharp-sdk`, Workflow File `release.yml` and Environment `release`.
3. Preview the readme now that the repository is public (before that, its images and badges do not load). Upload the `.nupkg` of a CI `packages` artifact on nuget.org's Upload Package page, read the Readme File section of the preview, and cancel.
4. Unlist or deprecate `AgentCat` 0.0.1 on nuget.org, the placeholder that reserved the name.
5. Release `1.0.0` with the steps below, which push the tag `v1.0.0`.
6. Once nuget.org lists the package, merge agentcat-ui PR #255 (the C# pages of docs.agentcat.com).

## Each release

1. On a branch: set the version in `Directory.Build.props` (`VersionPrefix`, and `VersionSuffix` for a preview, empty for a stable release). The tests and the `pack` job read the expected versions from the props files. If you changed a package pin, regenerate `src/AgentCat/packages.lock.json` with `dotnet restore src/AgentCat/AgentCat.csproj -p:RestoreForceEvaluate=true`, because the locked restore of CI fails until you do. In `CHANGELOG.md`, move the entries of `[Unreleased]` under `## [<version>] - YYYY-MM-DD`, add the link reference of `[<version>]` at the bottom, and point the `[Unreleased]` compare link at `v<version>...HEAD`.
2. Run `python3 .github/scripts/release.py prepare` (or `prepare YYYY-MM-DD` for another day than today, UTC). It fills the date of that section and moves the entries of `src/AgentCat/PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`.
3. Check that `dotnet build -c Release` has no warnings, commit, and merge the pull request to `main` once CI is green.
4. Tag the merged commit on `main` and push the tag: `git tag v<version> <commit>` and `git push origin v<version>`.
5. If the run fails before the push, delete the tag (`git push --delete origin v<version>`), fix, and tag again. After the push started, keep the tag:
   - If the first push fails because nuget.org already has the version, the package did not come from this run. Find out where it came from before you do anything else.
   - If the `.nupkg` is on nuget.org but its symbols are not, push `AgentCat.<version>.snupkg` from the run's `packages` artifact by hand (`dotnet nuget push … --source https://api.nuget.org/v3/index.json` with an API key). Then re-run the failed jobs: a re-run skips the package that is already there and creates the GitHub release.

A version on nuget.org is final: a fix, including a fix of the readme, needs a new version.

The package readme is a plain Markdown copy of `README.md` that the pack writes (`.github/scripts/nuget_readme.py`). Every published readme loads `docs/static/*` and `docs/cats/*` from `main`, links to the README headings Getting Started, Why use AgentCat? and Free for open source, and links to the docs.agentcat.com pages that `README.md` names (`sdk/csharp/aspnet-core`, `sdk/csharp/mcp-server-create`, `sdk/csharp/operations` with its `#testing`, `sdk/csharp/limits`). Never move or rename them.

## The lock file and the SDK

CI restores `src/AgentCat` in locked mode, so a restore that resolves a package version other than the one in `src/AgentCat/packages.lock.json` fails. `Directory.Packages.props` pins the packages that `src/AgentCat` references, `Microsoft.NET.ILLink.Tasks` included. That package holds the trim and AOT analyzers. Without the pin, the SDK references it at the version that the SDK bundles, which changes with every SDK patch, and CI installs the newest SDK patch. To take newer analyzers:

1. Find the versions that a newer SDK bundles: the `KnownILLinkPack` items for `net8.0` and `net10.0` in `sdk/<version>/Microsoft.NETCoreSdk.BundledVersions.props` of its installation.
2. Set the two `Microsoft.NET.ILLink.Tasks` versions in `Directory.Packages.props` to them (8.0.x for `net8.0`, 10.0.x for `net10.0`).
3. Regenerate the lock file as in step 1 of "Each release", and check that `dotnet build -c Release` has no warnings.
