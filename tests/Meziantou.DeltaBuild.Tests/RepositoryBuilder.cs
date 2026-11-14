using System.Diagnostics;
using Meziantou.Framework;

namespace Meziantou.DeltaBuild.Tests;

internal sealed class RepositoryBuilder : IAsyncDisposable
{
    private readonly TemporaryDirectory _directory;
    private readonly List<string> _commits = [];

    public RepositoryBuilder()
    {
        _directory = TemporaryDirectory.Create();
    }

    public IReadOnlyList<string> Commits => _commits;

    public FullPath RepositoryPath => _directory.FullPath;

    public RepositoryBuilder CreateCommit(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var fullPath = _directory.FullPath / path;
            fullPath.CreateParentDirectory();
            File.WriteAllText(fullPath, content);
        }

        RunGit("add", "-A");
        RunGit("commit", "-m", $"Commit {_commits.Count + 1}", "--allow-empty");

        var sha = RunGit("rev-parse", "HEAD").Trim();
        _commits.Add(sha);

        return this;
    }

    /// <summary>
    /// Writes files to the working directory without committing them.
    /// Useful for testing working-tree comparison mode.
    /// </summary>
    public RepositoryBuilder WriteFiles(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var fullPath = _directory.FullPath / path;
            fullPath.CreateParentDirectory();
            File.WriteAllText(fullPath, content);
        }

        return this;
    }

    public string HeadCommit => RunGit("rev-parse", "HEAD").Trim();

    /// <summary>
    /// Checks out a new branch starting at the given commit (detached when <paramref name="branchName"/> is null).
    /// </summary>
    public RepositoryBuilder Checkout(string? branchName, string commitSha)
    {
        if (branchName is null)
        {
            RunGit("checkout", "--detach", commitSha);
        }
        else
        {
            RunGit("checkout", "-B", branchName, commitSha);
        }

        return this;
    }

    /// <summary>
    /// Merges the given commit into the current HEAD with a merge commit (the current HEAD is the first parent).
    /// </summary>
    public RepositoryBuilder Merge(string commitSha, string? message = null)
    {
        RunGit("merge", "--no-ff", "--no-edit", "-m", message ?? $"Merge {commitSha}", commitSha);
        _commits.Add(HeadCommit);
        return this;
    }

    public RepositoryBuilder SetRemoteTrackingBranch(string branchName, string commitSha)
    {
        RunGit("update-ref", $"refs/remotes/origin/{branchName}", commitSha);
        return this;
    }

    public RepositoryBuilder SetDefaultRemoteBranch(string branchName)
    {
        RunGit("symbolic-ref", "refs/remotes/origin/HEAD", $"refs/remotes/origin/{branchName}");
        return this;
    }

    public async Task InitializeAsync()
    {
        RunGit("init");
        RunGit("config", "user.email", "test@test.com");
        RunGit("config", "user.name", "Test User");
        // Create initial empty commit so merge-base can work
        RunGit("commit", "--allow-empty", "-m", "Initial commit");
        var sha = RunGit("rev-parse", "HEAD").Trim();
        _commits.Add(sha);
    }

    private string RunGit(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _directory.FullPath,
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
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    public ValueTask DisposeAsync()
    {
        _directory.Dispose();
        return ValueTask.CompletedTask;
    }
}
