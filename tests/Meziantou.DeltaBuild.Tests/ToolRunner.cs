using System.Diagnostics;
using System.Text;
using Xunit;

namespace Meziantou.DeltaBuild.Tests;

internal static class ToolRunner
{
    public static async Task<string> RunToolAsync(
        ITestOutputHelper output,
        params string[] args)
    {
        // Find the tool assembly
        var toolAssemblyPath = GetToolAssemblyPath();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(toolAssemblyPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add(toolAssemblyPath);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        output.WriteLine($"Running: dotnet {toolAssemblyPath} {string.Join(' ', args)}");

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.WriteLine($"[stdout] {e.Data}");
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.WriteLine($"[stderr] {e.Data}");
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var errorOutput = stderr.ToString();
            Assert.Fail(
                $"Tool exited with code {process.ExitCode}.\nStdout:\n{stdout}\nStderr:\n{errorOutput}");
        }

        return stdout.ToString();
    }

    private static string GetToolAssemblyPath()
    {
        // Navigate from the test assembly location to the tool's output
        var testAssemblyDir = Path.GetDirectoryName(typeof(ToolRunner).Assembly.Location)!;

        // Go up to the repo root (tests/Meziantou.DeltaBuild.Tests/bin/Debug/net10.0 -> root)
        var repoRoot = Path.GetFullPath(Path.Combine(testAssemblyDir, "..", "..", "..", "..", ".."));
        var toolProject = Path.Combine(repoRoot, "src", "Meziantou.DeltaBuild");

        // Find the built assembly in the tool's output directory
        // Use the same configuration as the test project
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var toolAssembly = Path.Combine(toolProject, "bin", Configuration, "net10.0", "Meziantou.DeltaBuild.dll");

        if (!File.Exists(toolAssembly))
        {
            throw new FileNotFoundException(
                $"Tool assembly not found at {toolAssembly}. Ensure the tool project is built before running tests.",
                toolAssembly);
        }

        return toolAssembly;
    }
}
