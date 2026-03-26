using System.Diagnostics;

namespace Meziantou.DeltaBuild;

internal static class GitHelper
{
    public static async Task<string> GetHeadCommitAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(repositoryPath, ["rev-parse", "HEAD"], cancellationToken);
        return result.Trim();
    }

    public static async Task<string> GetDefaultBranchAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // Returns something like "refs/remotes/origin/main"
        var result = await RunGitAsync(repositoryPath, ["symbolic-ref", "refs/remotes/origin/HEAD"], cancellationToken);
        var fullRef = result.Trim();

        // Strip "refs/remotes/" prefix to get "origin/main"
        const string prefix = "refs/remotes/";
        if (fullRef.StartsWith(prefix, StringComparison.Ordinal))
        {
            return fullRef[prefix.Length..];
        }

        return fullRef;
    }

    public static async Task<string> GetMergeBaseAsync(string repositoryPath, string commitA, string commitB, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(repositoryPath, ["merge-base", commitA, commitB], cancellationToken);
        return result.Trim();
    }

    public static async Task<IReadOnlyList<string>> GetChangedFilesAsync(string repositoryPath, string baseCommit, string headCommit, CancellationToken cancellationToken = default)
    {
        // Use -z for NUL-delimited output (safe for filenames with spaces/special chars)
        var result = await RunGitAsync(repositoryPath, ["diff", "--name-only", "-z", baseCommit, headCommit], cancellationToken);

        if (string.IsNullOrEmpty(result))
        {
            return [];
        }

        // Split by NUL character, filter empty entries (trailing NUL produces one)
        return result.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public static async Task<string> RunGitAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        process.Start();

        // Read stdout and stderr as raw streams to preserve NUL characters (for -z output)
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var errorMessage = stderr.Trim();
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {errorMessage}");
        }

        return stdout;
    }
}
