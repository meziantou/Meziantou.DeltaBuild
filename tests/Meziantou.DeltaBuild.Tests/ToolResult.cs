namespace Meziantou.DeltaBuild.Tests;

internal sealed record ToolResult(int ExitCode, string Stdout, string Stderr);
