# Releasing

## Release a version

Every change that a user can see adds an entry to the `[Unreleased]` section of `CHANGELOG.md` in its pull request. The release workflow (`.github/workflows/release.yml`) does the rest, in two steps with a pull request between them:

1. In GitHub, open **Actions > release > Run workflow** on `main`. Keep `patch`, or type `minor`, `major` or an exact version such as `1.2.0`. The words count from the newest `v*` tag. The first release has no tag yet: it is the version that `Directory.Build.props` holds.
2. The workflow opens the pull request "Release vX.Y.Z". It sets `VersionPrefix` in `Directory.Build.props`, turns `[Unreleased]` into the dated section of the version, and moves the entries of `src/AgentCat/PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`. It also starts CI on the pull request. An `[Unreleased]` section with no entry, a version that nuget.org already has, or a pin of `AgentCat.Api` 0.0.4 stops the workflow.
3. Read the pull request, and merge it when CI passes.
4. The merge starts the workflow again. It waits for CI on the merge commit, and pushes the package and the symbols that this CI run built and tested to nuget.org with trusted publishing. Then it creates the tag `vX.Y.Z` and its GitHub release, with the CHANGELOG section as the notes.

If a step fails, fix the cause and re-run the failed jobs of that run. Only the run of the release commit (the merge commit that adds the dated section) publishes, so a later push to `main` cannot publish another commit under that version: its run only warns. A re-run skips a version that nuget.org already has, and the tag comes last. On the first attempt, a version that nuget.org already has stops the push: the package did not come from this run, so find out where it came from before you do anything else. If nuget.org has the `.nupkg` but not its symbols, push `AgentCat.<version>.snupkg` from the `packages` artifact of the CI run by hand (`dotnet nuget push … --source https://api.nuget.org/v3/index.json` with an API key).

To run a step of the workflow by hand, use `python3 .github/scripts/release.py prepare <patch | minor | major | X.Y.Z> <YYYY-MM-DD>`, `status` or `notes <X.Y.Z>`. The `scripts` job of CI tests these steps.

The workflow releases `X.Y.Z` versions only, so `Directory.Build.props` sets no `VersionSuffix`. The tests and the `pack` job read the expected version from the props files.

### One-time setup

- In the repository settings, **Actions > General > Allow GitHub Actions to create and approve pull requests** is on, so that the workflow can open the release pull request.
- The repository has a `release` environment, limited to the `main` branch, with the environment secret `NUGET_USER`: the nuget.org profile name (not the email) of the account that owns `AgentCat`.
- On nuget.org, a Trusted Publishing policy names the repository owner `agentcathq`, the repository `agentcat-csharp-sdk`, the workflow file `release.yml` and the environment `release`.
- No ruleset limits who can create `v*` tags, because the workflow creates the tag with its `GITHUB_TOKEN`.

## The package on nuget.org

A version on nuget.org is final: a fix, including a fix of the readme, needs a new version.

The package readme is a plain Markdown copy of `README.md` that the pack writes (`.github/scripts/nuget_readme.py`). Every published readme loads `docs/static/*` and `docs/cats/*` from `main`, links to the README headings Getting Started, Why use AgentCat? and Free for open source, and links to the docs.agentcat.com pages that `README.md` names (`sdk/csharp/aspnet-core`, `sdk/csharp/mcp-server-create`, `sdk/csharp/operations` with its `#testing`, `sdk/csharp/limits`). Never move or rename them.

## The lock file and the SDK

CI restores `src/AgentCat` in locked mode, so a pull request that changes a package pin must regenerate the lock file (step 3 below). A restore that resolves a package version other than the one in `src/AgentCat/packages.lock.json` fails. `Directory.Packages.props` pins the packages that `src/AgentCat` references, `Microsoft.NET.ILLink.Tasks` included. That package holds the trim and AOT analyzers. Without the pin, the SDK references it at the version that the SDK bundles, which changes with every SDK patch, and CI installs the newest SDK patch. To take newer analyzers:

1. Find the versions that a newer SDK bundles: the `KnownILLinkPack` items for `net8.0` and `net10.0` in `sdk/<version>/Microsoft.NETCoreSdk.BundledVersions.props` of its installation.
2. Set the two `Microsoft.NET.ILLink.Tasks` versions in `Directory.Packages.props` to them (8.0.x for `net8.0`, 10.0.x for `net10.0`).
3. Regenerate the lock file with `dotnet restore src/AgentCat/AgentCat.csproj -p:RestoreForceEvaluate=true`, and check that `dotnet build -c Release` has no warnings.
