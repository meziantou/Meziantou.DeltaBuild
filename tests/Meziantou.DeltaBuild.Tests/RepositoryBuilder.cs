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

    public string RepositoryPath => _directory.FullPath;

    public RepositoryBuilder CreateCommit(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(_directory.FullPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
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
            var fullPath = Path.Combine(_directory.FullPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

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
