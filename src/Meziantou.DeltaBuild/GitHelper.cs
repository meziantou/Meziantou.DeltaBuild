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
        const string Prefix = "refs/remotes/";
        if (fullRef.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return fullRef[Prefix.Length..];
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

    /// <summary>
    /// Gets all files that differ between the given base commit and the current working tree,
    /// including staged changes, unstaged modifications, and untracked files.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetWorkingTreeChangedFilesAsync(string repositoryPath, string baseCommit, CancellationToken cancellationToken = default)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);

        // 1. Tracked files changed between the base commit and the working tree (staged + unstaged)
        var diffResult = await RunGitAsync(repositoryPath, ["diff", "--name-only", "-z", baseCommit], cancellationToken);
        if (!string.IsNullOrEmpty(diffResult))
        {
            foreach (var file in diffResult.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                files.Add(file);
            }
        }

        // 2. Untracked files (new files not yet staged)
        var untrackedResult = await RunGitAsync(repositoryPath, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken);
        if (!string.IsNullOrEmpty(untrackedResult))
        {
            foreach (var file in untrackedResult.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                files.Add(file);
            }
        }

        return [.. files];
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
