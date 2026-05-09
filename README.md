# Meziantou.DeltaBuild

A .NET CLI tool that generates subset solution or build files for **incremental CI builds** in monorepos. Instead of building everything on every pull request, DeltaBuild analyzes git changes and the MSBuild project dependency graph to determine which projects are actually affected, then produces a filtered solution/build file containing only those projects.

## Why?

In a monorepo, a single repository hosts many projects. A typical CI pipeline rebuilds *all* of them on every commit or pull request, even when a change only touches one leaf project. This wastes time and compute resources.

**Meziantou.DeltaBuild** solves this by:

1. Comparing two git commits to find changed files.
2. Building the MSBuild project dependency graph from your solution or traversal file.
3. Mapping changed files to the projects that own them (source files, imports, `.props`/`.targets`, `.editorconfig`, NuGet configs, etc.).
4. Including transitive dependents — if a library changed, every project that references it (directly or indirectly) is included.
5. Writing a new solution/build file that contains **only the affected projects**.

Your CI pipeline then builds this smaller file instead of the full solution, dramatically reducing build and test times for most pull requests.

## Installation

```bash
dotnet tool install --global Meziantou.DeltaBuild
```

Or as a local tool:

```bash
dotnet tool install Meziantou.DeltaBuild
```

## Usage

```bash
Meziantou.DeltaBuild generate --input <path> --output <path> [options]
```

### Basic example

```bash
Meziantou.DeltaBuild generate \
  --input MyRepo.sln \
  --output DeltaBuild.sln
```

This compares `HEAD` against the merge-base of the default remote branch, finds changed files, determines which projects are affected, and writes a filtered `DeltaBuild.sln`.

### CI example (Azure DevOps / GitHub Actions)

```bash
Meziantou.DeltaBuild generate \
  --input dirs.proj \
  --output delta.proj \
  --repository .
```

On GitHub Actions `pull_request` events, DeltaBuild automatically uses `GITHUB_BASE_REF` to compute the merge base (`git merge-base HEAD origin/<base-ref>`), so no explicit `--base-commit` is needed.

Then build only the affected projects:

```bash
dotnet build delta.proj
dotnet test delta.proj
```

### Local development (working tree)

Compare your uncommitted changes (staged, unstaged, and untracked files) against a branch:

```bash
Meziantou.DeltaBuild generate \
  --input MyRepo.sln \
  --output delta.sln \
  --base-commit $(git merge-base origin/main HEAD) \
  --working-tree
```

This is useful for quickly checking which projects your local modifications affect before committing.

### Shard test projects for parallel CI

```bash
Meziantou.DeltaBuild generate \
  --input MyRepo.sln \
  --output delta.proj \
  --test-projects-only \
  --shard 1 \
  --total-shards 3
```

This writes one file (`delta.proj`) containing only the selected shard (1-based index) of affected test projects.

## Parameters

### Required

| Parameter | Alias | Description |
|-----------|-------|-------------|
| `--input` | `-i` | Path to the input file. Supported formats: `.sln`, `.slnx`, `.proj` (Traversal SDK), or a single project file (`.csproj`, `.fsproj`, `.vbproj`). |
| `--output` | `-o` | Path for the output file. The format is inferred from the file extension: `.sln`, `.slnx`, `.proj`, `.json`, or `.txt`. |

### Optional

| Parameter | Alias | Default | Description |
|-----------|-------|---------|-------------|
| `--repository` | `-r` | `.` (current directory) | Path to the git repository root. |
| `--head-commit` | | `HEAD` | The head commit SHA to compare. Ignored when `--working-tree` is set. |
| `--base-commit` | | Auto-detected | The base commit SHA. When omitted, computed via `git merge-base` using `--base-branch`. |
| `--base-branch` | | Auto-detected from GitHub Actions PR context or remote | The base branch name used for merge-base detection (e.g., `main`, `origin/main`). On GitHub Actions pull request events, defaults to `origin/$GITHUB_BASE_REF`. |
| `--working-tree` | | `false` | Compare the base commit against the current working directory instead of a commit. Includes staged, unstaged, and untracked files. When set, `--head-commit` is ignored. |
| `--include` | | *(all projects)* | Glob patterns to filter which projects to consider. Repeatable. Only projects matching at least one pattern are included. |
| `--test-projects-only` | | `false` | Only include projects where the MSBuild property `IsTestProject` is `true`. |
| `--shard` | | *(none)* | Generate only shard number `N` (1-based). Must be used with `--total-shards`. |
| `--total-shards` | | *(none)* | Total number of shards used to partition affected projects. Must be used with `--shard`. |
| `--no-output-if-empty` | | `false` | Do not write an output file when no projects are affected. If the output file already exists, it is deleted. |
| `--full-rebuild-trigger` | | *(none)* | Glob patterns for files that trigger a **full rebuild of all projects**. When any changed file matches, every project is included in the output. Repeatable. Replaces defaults when provided. |
| `--hierarchical-rebuild-trigger` | | `**/global.json`, `**/nuget.config`, `**/NuGet.config`, `**/NuGet.Config`, `**/.editorconfig` | Glob patterns for files that trigger a rebuild of **projects in the same folder hierarchy**. For example, changing `src/global.json` rebuilds projects under `src/` but not under `tests/`. A match at the repository root affects all projects. Repeatable. Replaces defaults when provided. |
| `--project-bundle` | | *(none)* | Comma-separated exact project paths that must be built together as a bundle. Repeatable. Paths are relative to the repository root unless absolute. Example: `--project-bundle src/B/B.csproj,src/C/C.csproj`. |
| `--engine` | | `MSBuild` | The analysis engine to use (see below). |
| `--traversal-before-import` | | `<output-name>.before.proj` | Path of the import added before the `<ProjectReference>` items in the generated Traversal SDK file. |
| `--traversal-sdk-version` | | *(none)* | Optional version appended to the Traversal SDK in generated Traversal SDK files. When set to `x.y.z`, generated files use `Sdk="Microsoft.Build.Traversal/x.y.z"`. |
| `--traversal-after-import` | | `<output-name>.after.proj` | Path of the import added after the `<ProjectReference>` items in the generated Traversal SDK file. |

### Analysis engines

| Engine | Description |
|--------|-------------|
| `MSBuild` | Default. Passes individual project paths as entry points to the MSBuild Static Graph API. Works with all input formats. |
| `RoslynWorkspace` | Uses Roslyn's `MSBuildWorkspace` to load projects. More compatible because Roslyn sees files added dynamically by MSBuild targets (e.g., source generators). Also tracks `.editorconfig` and `.globalconfig` via `AnalyzerConfigDocuments`. |
| `StaticGraph` | Passes the input file (solution or traversal) as a single entry point to MSBuild's `ProjectGraph`, letting MSBuild handle solution/traversal parsing natively with parallel evaluation. Inspired by [Petabridge/Incrementalist](https://github.com/petabridge/Incrementalist). |

## How it works

1. **Resolve commits** — Determines the base and head commits. If not provided, head defaults to `HEAD`. Base is computed via `git merge-base` using `origin/$GITHUB_BASE_REF` on GitHub Actions pull request events, otherwise using the default remote branch.
2. **Get changed files** — Runs `git diff --name-only` between the two commits.
3. **Parse input** — Reads the solution, traversal, or project file to discover the list of projects.
4. **Filter projects** — Applies `--include` glob patterns if provided.
5. **Check full-rebuild triggers** — If any changed file matches a `--full-rebuild-trigger` pattern, all projects are included and analysis stops.
6. **Check hierarchical-rebuild triggers** — For each changed file matching a `--hierarchical-rebuild-trigger` pattern, projects in the same folder hierarchy are marked as affected. For example, `src/nuget.config` affects projects under `src/`, while a root-level `nuget.config` affects all projects.
7. **Analyze project graph** — Uses the selected engine to build the dependency graph and determine which files each project owns (source files, imports, `.props`, `.targets`, `.editorconfig`, `.globalconfig`, etc.).
8. **Determine directly affected projects** — A project is directly affected if any of its owned files appears in the changed file list, or if it was flagged by a hierarchical trigger.
9. **Expand impacted projects** — Walks up the dependency graph to include transitive dependents, and applies `--project-bundle` rules so if one project in a bundle is affected, all bundle members are included too.
10. **Filter test projects (optional)** — When `--test-projects-only` is set, keeps only projects with `IsTestProject=true`.
11. **Write output** — Produces the filtered solution/build file containing only the final affected projects. When `--shard` and `--total-shards` are set, only the selected shard is written to the provided `--output` path. If `--no-output-if-empty` is set and no project is affected in the selected result, the output file is skipped/deleted.

## Output formats

| Extension | Format |
|-----------|--------|
| `.sln` | Visual Studio solution (v12) |
| `.slnx` | XML-based solution (Visual Studio 2022+) |
| `.proj` | MSBuild Traversal SDK project with `<ProjectReference>` items. All generated paths are prefixed with `$(MSBuildThisFileDirectory)` for stable path resolution. Automatically imports `<output>.before.proj` and `<output>.after.proj` if they exist, allowing you to inject custom MSBuild logic. |
| `.json` | JSON array of affected project paths |
| `.txt` | One project path per line |

## Tracked file types

DeltaBuild tracks the following MSBuild item types as owned files for each project:

- `Compile` — Source code files (`.cs`, `.fs`, `.vb`, etc.)
- `Content`, `None`, `EmbeddedResource` — Static assets and resources
- `AdditionalFiles` — Files passed to analyzers
- `EditorConfigFiles` — `.editorconfig` files discovered by MSBuild
- `GlobalAnalyzerConfigFiles` — `.globalconfig` files
- `Page`, `ApplicationDefinition`, `Resource` — WPF/XAML items
- `TypeScriptCompile` — TypeScript files
- **Import paths** — `.props`, `.targets`, and other imported MSBuild files (via `ProjectInstance.ImportPaths`)
- **Project file itself** — The `.csproj`/`.fsproj`/`.vbproj` file

The `RoslynWorkspace` engine additionally tracks files exposed through Roslyn's `Documents`, `AdditionalDocuments`, and `AnalyzerConfigDocuments` collections.
