# Copilot instructions for Meziantou.DeltaBuild

## Repository overview

- This repository contains a .NET CLI tool that computes the affected projects in a git diff and generates filtered `.sln`, `.slnx`, `.proj`, `.json`, or `.txt` outputs for incremental CI builds.
- Main product code is in `src/Meziantou.DeltaBuild`.
- Tests are in `tests/Meziantou.DeltaBuild.Tests` and mostly exercise the CLI by creating temporary git repositories.
- If you change user-visible behavior, keep the CLI help text, tests, and README aligned.

## Core expectations

- Prefer the smallest correct change. Avoid unrelated refactors.
- Preserve existing public behavior unless the task explicitly requires a behavior change.
- Any code you commit SHOULD compile, and new and existing tests related to the change SHOULD pass.
- You MUST make your best effort to verify the final state after your last edit.
- Do not claim success unless the relevant validation actually passed.
- If you could not build or test, say so explicitly and explain why.

## Validation requirements

- Re-run the relevant build and tests after the final edit. Do not rely on an earlier run.
- Do not assume a fix worked without rerunning the affected tests.
- Do not assume tests ran just because `dotnet test` returned success; verify that the intended tests were executed.
- When running tests, you can ignore warnings about the "Blame" data collector.
- When running tests, use the environment variable `DiffEngine_Disabled=true`.
- For repository-wide validation from the repo root, prefer:
	- `dotnet build Meziantou.DeltaBuild.slnx`
	- `dotnet test Meziantou.DeltaBuild.slnx`
- For focused validation, use filtered test runs when appropriate, but make sure the filter still covers the changed behavior.
- If the change is documentation-only or otherwise cannot affect the build, state that clearly in the final response instead of pretending validation was necessary.

## C# and repository conventions

- Follow `.editorconfig` if it exists in the checkout. If it does not, match the surrounding code style.
- Prefer file-scoped namespace declarations and single-line using directives.
- Ensure the final return statement of a method is on its own line.
- Use pattern matching and switch expressions where they improve clarity.
- Use `nameof` instead of string literals when referring to member names.
- Always use `is null` or `is not null` instead of `== null` or `!= null`.
- Trust the C# null annotations; do not add redundant null checks.
- Prefer `?.` when appropriate.
- Use `ObjectDisposedException.ThrowIf` where applicable.
- Do not introduce trailing whitespace.
- Add a blank line before XML documentation comments (`///`) when they follow other code.
- Do not update `global.json`.

## Test conventions

- Prefer adding tests to existing files instead of creating new test files without a good reason.
- For behavioral changes, prefer extending `tests/Meziantou.DeltaBuild.Tests/DeltaBuildTests.cs`.
- Reuse existing helpers such as `RepositoryBuilder`, `ToolRunner`, and inline snapshot assertions.
- Keep tests readable and scenario-based.
- Do not add "Act", "Arrange", or "Assert" comments.
- Do not disable, comment out, or weaken tests to make them pass.

## Change-specific guidance

- If you modify command-line options or defaults, update:
	- `Program.cs`
	- relevant tests
	- `README.md` when the user-facing behavior changes
- If you modify project graph analysis or affected-file detection, consider the impact on all supported engines: `MSBuild`, `RoslynWorkspace`, and `StaticGraph`.
- If you modify generated output, update or add assertions that validate the exact output format.

## Final response requirements

- Summarize the files changed and the reason for the change.
- Report exactly what you validated.
- If validation was not run, say that explicitly.
- Do not say the task is complete if relevant build or test validation failed.