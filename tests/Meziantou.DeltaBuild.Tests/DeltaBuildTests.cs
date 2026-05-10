using Meziantou.Framework.InlineSnapshotTesting;
using Xunit;

namespace Meziantou.DeltaBuild.Tests;

public sealed class DeltaBuildTests(ITestOutputHelper output) : IAsyncDisposable
{
    private readonly List<RepositoryBuilder> _repos = [];

    private async Task<RepositoryBuilder> CreateRepositoryAsync()
    {
        var builder = new RepositoryBuilder();
        _repos.Add(builder);
        await builder.InitializeAsync();
        return builder;
    }

    private Task<string> RunTool(params string[] args)
    {
        return ToolRunner.RunToolAsync(output, args);
    }

    private Task<string> RunTool(IReadOnlyDictionary<string, string?> environmentVariables, params string[] args)
    {
        return ToolRunner.RunToolAsync(output, environmentVariables, args);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var repo in _repos)
        {
            await repo.DisposeAsync();
        }
    }

    [Fact]
    public async Task SingleProject_FileChanged_ProjectIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project with a source file
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("Hello");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the source file
        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("Hello, World!");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task MultipleProjects_OnlyChangedProjectIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Only modify App1
        repo.CreateCommit(
            ("src/App1/Program.cs", """
                Console.WriteLine("App1 modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task TransitiveDependents_AreIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create Lib -> App dependency chain
        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("src/Unrelated/Unrelated.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Dummy.cs", """
                namespace Unrelated;
                public class Dummy { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/Unrelated/Unrelated.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the Lib project source
        repo.CreateCommit(
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { public int Value { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Both Lib and App should be included (App depends on Lib transitively)
        // Unrelated should NOT be included
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task FullRebuildTrigger_DirectoryBuildProps_AllProjectsIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify Directory.Build.props (implicitly imported by all projects)
        repo.CreateCommit(
            ("Directory.Build.props", """
                <Project>
                  <PropertyGroup>
                    <LangVersion>latest</LangVersion>
                  </PropertyGroup>
                </Project>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // All projects should be included because Directory.Build.props is imported by all projects
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App2/App2.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("nuget.config")]
    [InlineData("NuGet.config")]
    [InlineData("NuGet.Config")]
    public async Task FullRebuildTrigger_NuGetConfigCasing_AllProjectsIncluded(string nugetConfigFileName)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Add/modify the NuGet config file with the given casing
        repo.CreateCommit(
            (nugetConfigFileName, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // All projects should be included because nuget config is a full-rebuild trigger
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App2/App2.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task CustomFullRebuildTrigger_ReplacesDefaults()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify Directory.Build.props — implicitly imported by MSBuild
        repo.CreateCommit(
            ("Directory.Build.props", """
                <Project>
                  <PropertyGroup>
                    <LangVersion>latest</LangVersion>
                  </PropertyGroup>
                </Project>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        // Use custom trigger that does NOT include Directory.Build.props
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--full-rebuild-trigger", "**/custom-trigger.txt");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // App is affected because Directory.Build.props is implicitly imported by MSBuild
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task MultipleFullRebuildTriggers_AnyMatchTriggersFullRebuild()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify a file matching the second trigger pattern
        repo.CreateCommit(
            ("eng/Build.ps1", "# build script")
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
          "--full-rebuild-trigger", ".github/**/*",
          "--full-rebuild-trigger", "eng/**/*");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // All projects should be included because eng/Build.ps1 matches the "eng/**/*" trigger
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App2/App2.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task FullRebuildTrigger_DirectoryGlobPattern_MatchesFilesInDirectory()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify a file inside the .github directory
        repo.CreateCommit(
            (".github/workflows/ci.yml", "# CI workflow")
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
          "--full-rebuild-trigger", ".github/**/*");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // All projects should be included because .github/workflows/ci.yml matches the ".github/**/*" trigger
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App2/App2.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task IncludeGlobFilter_FiltersProjects()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create projects in different folders
        repo.CreateCommit(
            ("src/Feature1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Feature1/Program.cs", """
                Console.WriteLine("Feature1");
                """),
            ("src/Feature2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Feature2/Program.cs", """
                Console.WriteLine("Feature2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/Feature1/App1.csproj" />
                    <ProjectReference Include="src/Feature2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify both projects
        repo.CreateCommit(
            ("src/Feature1/Program.cs", """
                Console.WriteLine("Feature1 modified");
                """),
            ("src/Feature2/Program.cs", """
                Console.WriteLine("Feature2 modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        // Only include Feature1 projects
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--include", "src/Feature1/**/*.*proj");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Feature1/App1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task NoChanges_EmptyOutput()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: No changes (empty commit)
        repo.CreateCommit();

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        var stdout = await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        Assert.Contains("No changed files detected. Skipping project graph analysis.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Analyzing projects using engine:", stdout, StringComparison.Ordinal);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup />
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task NoChanges_NoOutputIfEmpty_DoesNotCreateOutput()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit();

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--no-output-if-empty");

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task NoChanges_NoOutputIfEmpty_DeletesExistingOutputFile()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit();

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await File.WriteAllTextAsync(outputPath, "existing output", TestContext.Current.CancellationToken);

        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--no-output-if-empty");

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task TraversalOutput_HasConditionalBeforeAndAfterImports()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the source file
        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("Modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task TraversalOutput_CustomImportNames()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the source file
        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("Modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--traversal-before-import", "custom.before.props",
            "--traversal-after-import", "custom.after.targets");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)custom.before.props" Condition="Exists('$(MSBuildThisFileDirectory)custom.before.props')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)custom.after.targets" Condition="Exists('$(MSBuildThisFileDirectory)custom.after.targets')" />
            </Project>
            """);
    }

    [Fact]
    public async Task TraversalOutput_DefaultSdkVersion_HasNoVersionSuffix()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a single project
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """)
        );

        // Commit 2: Modify the source file
        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("Modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "src/App/App.csproj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task TraversalOutput_CustomSdkVersion_AppendsVersionSuffix()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a single project
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """)
        );

        // Commit 2: Modify the source file
        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("Modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "src/App/App.csproj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--traversal-sdk-version", "4.1.82");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal/4.1.82">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task MixedProjectTypes_CsprojAndFsproj()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create C# and F# projects
        repo.CreateCommit(
            ("src/CSharpLib/CSharpLib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/CSharpLib/Class1.cs", """
                namespace CSharpLib;
                public class Class1 { }
                """),
            ("src/FSharpLib/FSharpLib.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Library.fs" />
                  </ItemGroup>
                </Project>
                """),
            ("src/FSharpLib/Library.fs", """
                module FSharpLib.Library
                let hello = "Hello"
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/CSharpLib/CSharpLib.csproj" />
                    <ProjectReference Include="src/FSharpLib/FSharpLib.fsproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Only modify the F# source
        repo.CreateCommit(
            ("src/FSharpLib/Library.fs", """
                module FSharpLib.Library
                let hello = "Hello, World!"
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/FSharpLib/FSharpLib.fsproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task SingleProjectInput_TransitiveClosureUsed()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create Lib -> App dependency
        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """)
        );

        // Commit 2: Modify Lib source
        repo.CreateCommit(
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { public int Value => 42; }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        // Use single project as input — it should discover Lib via transitive closure
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "src/App/App.csproj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Both App and Lib should be in the output (App depends on Lib)
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task TransitiveDependentChain_A_B_C()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create A -> B -> C dependency chain
        repo.CreateCommit(
            ("src/A/A.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/A/ClassA.cs", """
                namespace A;
                public class ClassA { }
                """),
            ("src/B/B.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../A/A.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/B/ClassB.cs", """
                namespace B;
                public class ClassB { }
                """),
            ("src/C/C.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../B/B.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/C/Program.cs", """
                Console.WriteLine("C");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/A/A.csproj" />
                    <ProjectReference Include="src/B/B.csproj" />
                    <ProjectReference Include="src/C/C.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify A
        repo.CreateCommit(
            ("src/A/ClassA.cs", """
                namespace A;
                public class ClassA { public int Id { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // All three should be included: A changed, B depends on A, C depends on B
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/A/A.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/B/B.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/C/C.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task JsonOutput_WritesArrayOfRelativePaths()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/App1/Program.cs", """
                Console.WriteLine("App1 modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.json");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content, """
            ["src/App1/App1.csproj"]
            """);
    }

    [Fact]
    public async Task TxtOutput_WritesOneProjectPerLine()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { public int Value => 42; }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.txt");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            src/App/App.csproj
            src/Lib/Lib.csproj
            """);
    }

    [Fact]
    public async Task TestProjectsOnly_OnlyIncludesProjectsWithIsTestProject()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("tests/Lib.Tests/Lib.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../src/Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("tests/Lib.Tests/Test1.cs", """
                namespace Lib.Tests;
                public class Test1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="tests/Lib.Tests/Lib.Tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { public int Value => 42; }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.txt");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--test-projects-only");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            tests/Lib.Tests/Lib.Tests.csproj
            """);
    }

    [Fact]
    public async Task FullRebuildTrigger_WithTestProjectsOnly_IncludesOnlyTestProjects()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("eng/build.ps1", """
                Write-Host "build v1"
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("tests/App.Tests/App.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                </Project>
                """),
            ("tests/App.Tests/Test1.cs", """
                namespace App.Tests;
                public class Test1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="tests/App.Tests/App.Tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("eng/build.ps1", """
                Write-Host "build v2"
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.txt");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--full-rebuild-trigger", "eng/**/*",
            "--test-projects-only");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            tests/App.Tests/App.Tests.csproj
            """);
    }

    [Fact]
    public async Task ShardAndTotalShards_WritesOnlyRequestedShard()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": {
                    "version": "10.0.203"
                  }
                }
                """),
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Class1.cs", """
                namespace App1;
                public class Class1 { }
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Class1.cs", """
                namespace App2;
                public class Class1 { }
                """),
            ("src/App3/App3.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App3/Class1.cs", """
                namespace App3;
                public class Class1 { }
                """),
            ("src/App4/App4.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App4/Class1.cs", """
                namespace App4;
                public class Class1 { }
                """),
            ("src/App5/App5.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App5/Class1.cs", """
                namespace App5;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                    <ProjectReference Include="src/App3/App3.csproj" />
                    <ProjectReference Include="src/App4/App4.csproj" />
                    <ProjectReference Include="src/App5/App5.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": {
                    "version": "10.0.204"
                  }
                }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.txt");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--shard", "2",
            "--total-shards", "3");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            src/App3/App3.csproj
            src/App4/App4.csproj
            """);

        Assert.False(File.Exists(Path.Combine(repo.RepositoryPath, "output.shard-1.txt")));
        Assert.False(File.Exists(Path.Combine(repo.RepositoryPath, "output.shard-2.txt")));
        Assert.False(File.Exists(Path.Combine(repo.RepositoryPath, "output.shard-3.txt")));
    }

    [Fact]
    public async Task ShardSeparate_WithMoreProjectsThanShards_DistributesInDeclarationOrder()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Class1.cs", """
                namespace App1;
                public class Class1 { }
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Class1.cs", """
                namespace App2;
                public class Class1 { }
                """),
            ("src/App3/App3.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App3/Class1.cs", """
                namespace App3;
                public class Class1 { }
                """),
            ("src/App4/App4.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App4/Class1.cs", """
                namespace App4;
                public class Class1 { }
                """),
            ("src/App5/App5.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App5/Class1.cs", """
                namespace App5;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                    <ProjectReference Include="src/App3/App3.csproj" />
                    <ProjectReference Include="src/App4/App4.csproj" />
                    <ProjectReference Include="src/App5/App5.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": {
                    "version": "10.0.204"
                  }
                }
                """)
        );

        var shard1OutputPath = Path.Combine(repo.RepositoryPath, "output.shard-1.txt");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", shard1OutputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--shard", "1",
            "--total-shards", "2",
            "--shard-separate", "src/App5/App5.csproj",
            "--shard-separate", "src/App1/App1.csproj",
            "--shard-separate", "src/App4/App4.csproj",
            "--shard-separate", "src/App2/App2.csproj",
            "--shard-separate", "src/App3/App3.csproj");

        var shard1Content = await File.ReadAllTextAsync(shard1OutputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(shard1Content.Trim(), """
            src/App3/App3.csproj
            src/App4/App4.csproj
            src/App5/App5.csproj
            """);

        var shard2OutputPath = Path.Combine(repo.RepositoryPath, "output.shard-2.txt");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", shard2OutputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--shard", "2",
            "--total-shards", "2",
            "--shard-separate", "src/App5/App5.csproj",
            "--shard-separate", "src/App1/App1.csproj",
            "--shard-separate", "src/App4/App4.csproj",
            "--shard-separate", "src/App2/App2.csproj",
            "--shard-separate", "src/App3/App3.csproj");

        var shard2Content = await File.ReadAllTextAsync(shard2OutputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(shard2Content.Trim(), """
            src/App1/App1.csproj
            src/App2/App2.csproj
            """);
    }

    [Fact]
    public async Task ShardAndTotalShards_WithTestProjectsOnly_WritesRequestedShard()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/Class1.cs", """
                namespace Lib;
                public class Class1 { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("tests/A.Tests/A.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../src/Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("tests/A.Tests/Test1.cs", """
                namespace A.Tests;
                public class Test1 { }
                """),
            ("tests/B.Tests/B.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../src/Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("tests/B.Tests/Test1.cs", """
                namespace B.Tests;
                public class Test1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="tests/A.Tests/A.Tests.csproj" />
                    <ProjectReference Include="tests/B.Tests/B.Tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/Lib/Class1.cs", """
                namespace Lib;
                public class Class1 { public int Value => 42; }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.txt");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--test-projects-only",
            "--shard", "1",
            "--total-shards", "3");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            tests/A.Tests/A.Tests.csproj
            """);

        Assert.False(File.Exists(Path.Combine(repo.RepositoryPath, "output.shard-1.txt")));
        Assert.False(File.Exists(Path.Combine(repo.RepositoryPath, "output.shard-2.txt")));
        Assert.False(File.Exists(Path.Combine(repo.RepositoryPath, "output.shard-3.txt")));
    }

    [Fact]
    public async Task ShardOptionWithoutTotalShards_FailsWithClearError()
    {
        var result = await ToolRunner.RunToolRawAsync(
            output,
            "generate",
            "--input", "input.proj",
            "--output", "output.txt",
            "--shard", "1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be used with --total-shards", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShardSeparateOptionWithoutShardAndTotalShards_FailsWithClearError()
    {
        var result = await ToolRunner.RunToolRawAsync(
            output,
            "generate",
            "--input", "input.proj",
            "--output", "output.txt",
            "--shard-separate", "tests/Proj/Proj.csproj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be used with --shard and --total-shards", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TotalShardsOptionWithoutShard_FailsWithClearError()
    {
        var result = await ToolRunner.RunToolRawAsync(
            output,
            "generate",
            "--input", "input.proj",
            "--output", "output.txt",
            "--total-shards", "3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be used with --shard", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShardOption_Zero_FailsWithClearError()
    {
        var result = await ToolRunner.RunToolRawAsync(
            output,
            "generate",
            "--input", "input.proj",
            "--output", "output.txt",
            "--shard", "0",
            "--total-shards", "3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be greater than 0", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TotalShardsOption_Zero_FailsWithClearError()
    {
        var result = await ToolRunner.RunToolRawAsync(
            output,
            "generate",
            "--input", "input.proj",
            "--output", "output.txt",
            "--shard", "1",
            "--total-shards", "0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be greater than 0", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShardOption_GreaterThanTotalShards_FailsWithClearError()
    {
        var result = await ToolRunner.RunToolRawAsync(
            output,
            "generate",
            "--input", "input.proj",
            "--output", "output.txt",
            "--shard", "4",
            "--total-shards", "3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be less than or equal to --total-shards", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiTargeting_FileChangedInOneTfm_ProjectIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a multi-targeting project with TFM-specific files
        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
                  </PropertyGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
                    <Compile Include="Legacy.cs" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                    <Compile Include="Modern.cs" />
                  </ItemGroup>
                </Project>
                """),
            ("src/Lib/Legacy.cs", """
                namespace Lib;
                public class Legacy { }
                """),
            ("src/Lib/Modern.cs", """
                namespace Lib;
                public class Modern { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify only the net9.0-specific file
        repo.CreateCommit(
            ("src/Lib/Legacy.cs", """
                namespace Lib;
                public class Legacy { public int Id => 1; }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Lib should be included because Legacy.cs belongs to Lib (under the net9.0 TFM)
        // App should NOT be included because it doesn't depend on Lib
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task MultiTargeting_FileChangedInOneTfm_TransitiveDependentIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Lib (multi-target) -> App depends on Lib
        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
                  </PropertyGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
                    <Compile Include="Net9Only.cs" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                    <Compile Include="Net10Only.cs" />
                  </ItemGroup>
                </Project>
                """),
            ("src/Lib/Net9Only.cs", """
                namespace Lib;
                public class Net9Only { }
                """),
            ("src/Lib/Net10Only.cs", """
                namespace Lib;
                public class Net10Only { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify only the net10.0-specific file in Lib
        repo.CreateCommit(
            ("src/Lib/Net10Only.cs", """
                namespace Lib;
                public class Net10Only { public string Name => "v2"; }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Both Lib and App should be included: Lib is directly affected, App depends on Lib
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task MultiTargeting_SharedFileChanged_ProjectIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Multi-targeting project with a shared file and TFM-specific files
        repo.CreateCommit(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
                  </PropertyGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
                    <Compile Include="Platform/Net9Impl.cs" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                    <Compile Include="Platform/Net10Impl.cs" />
                  </ItemGroup>
                </Project>
                """),
            ("src/Lib/Shared.cs", """
                namespace Lib;
                public class Shared { }
                """),
            ("src/Lib/Platform/Net9Impl.cs", """
                namespace Lib.Platform;
                public class Net9Impl { }
                """),
            ("src/Lib/Platform/Net10Impl.cs", """
                namespace Lib.Platform;
                public class Net10Impl { }
                """),
            ("src/Unrelated/Unrelated.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Dummy.cs", """
                namespace Unrelated;
                public class Dummy { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/Unrelated/Unrelated.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the shared file (not TFM-specific — globbed by both TFMs)
        repo.CreateCommit(
            ("src/Lib/Shared.cs", """
                namespace Lib;
                public class Shared { public bool Active => true; }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Lib should be included (Shared.cs is owned by Lib in both TFMs)
        // Unrelated should NOT be included
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task ImportedPropsFileChanged_ProjectIsAffected()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project that imports a .props file
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="..\..\build\Common.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("Hello");
                """),
            ("build/Common.props", """
                <Project>
                  <PropertyGroup>
                    <Deterministic>true</Deterministic>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Unrelated.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Class1.cs", """
                namespace Unrelated;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="src/Unrelated/Unrelated.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the imported .props file
        repo.CreateCommit(
            ("build/Common.props", """
                <Project>
                  <PropertyGroup>
                    <Deterministic>true</Deterministic>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                </Project>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        // Use a custom trigger to avoid the default **/*.props matching build/Common.props
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--full-rebuild-trigger", "**/non-existent-trigger.txt");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // App should be included because it imports Common.props which changed
        // Unrelated should NOT be included
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task RecursiveImports_DeepNestedPropsChanged_ProjectIsAffected()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a chain: App.csproj -> build/Common.props -> build/Shared.props -> build/Deep.props
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="..\..\build\Common.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("Hello");
                """),
            ("build/Common.props", """
                <Project>
                  <Import Project="Shared.props" />
                </Project>
                """),
            ("build/Shared.props", """
                <Project>
                  <Import Project="Deep.props" />
                </Project>
                """),
            ("build/Deep.props", """
                <Project>
                  <PropertyGroup>
                    <Deterministic>true</Deterministic>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Unrelated.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Class1.cs", """
                namespace Unrelated;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="src/Unrelated/Unrelated.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the deeply nested .props file
        repo.CreateCommit(
            ("build/Deep.props", """
                <Project>
                  <PropertyGroup>
                    <Deterministic>true</Deterministic>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                </Project>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--full-rebuild-trigger", "**/non-existent-trigger.txt");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // App should be included because it transitively imports Deep.props
        // (App.csproj -> Common.props -> Shared.props -> Deep.props)
        // Unrelated should NOT be included
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task CircularImport_HandledGracefully()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project with circular imports (A.props -> B.props -> A.props)
        // MSBuild silently skips re-importing a file that is already in the import chain
        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="..\\..\\build\\A.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("Hello");
                """),
            ("build/A.props", """
                <Project>
                  <Import Project="B.props" />
                </Project>
                """),
            ("build/B.props", """
                <Project>
                  <Import Project="A.props" />
                </Project>
                """),
            ("src/Unrelated/Unrelated.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Class1.cs", """
                namespace Unrelated;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="src/Unrelated/Unrelated.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify B.props (part of the circular chain)
        repo.CreateCommit(
            ("build/B.props", """
                <Project>
                  <Import Project="A.props" />
                  <PropertyGroup>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                </Project>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--full-rebuild-trigger", "**/non-existent-trigger.txt");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // App should be affected because it imports A.props -> B.props (which changed)
        // Unrelated should NOT be affected
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("RoslynWorkspace")]
    [InlineData("StaticGraph")]
    public async Task RoslynWorkspace_SingleProject_FileChanged_ProjectIncluded(string engine)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project with a source file
        repo.CreateCommit(
            ("global.json", """
                {
                  "msbuild-sdks": {
                    "Microsoft.Build.Traversal": "4.1.82"
                  }
                }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("Hello");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the source file
        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("Hello, World!");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--engine", engine);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("RoslynWorkspace")]
    [InlineData("StaticGraph")]
    public async Task RoslynWorkspace_TransitiveDependents_AreIncluded(string engine)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create Lib -> App dependency chain
        repo.CreateCommit(
            ("global.json", """
                {
                  "msbuild-sdks": {
                    "Microsoft.Build.Traversal": "4.1.82"
                  }
                }
                """),
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("src/Unrelated/Unrelated.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Dummy.cs", """
                namespace Unrelated;
                public class Dummy { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/Unrelated/Unrelated.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify only the Lib source file
        repo.CreateCommit(
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { public int Value { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--engine", engine);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Both Lib and App should be affected (App depends on Lib), Unrelated should not
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("RoslynWorkspace")]
    [InlineData("StaticGraph")]
    public async Task RoslynWorkspace_ImportedPropsChanged_ProjectIncluded(string engine)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project that imports a .props file
        repo.CreateCommit(
            ("global.json", """
                {
                  "msbuild-sdks": {
                    "Microsoft.Build.Traversal": "4.1.82"
                  }
                }
                """),
            ("shared/Shared.props", """
                <Project>
                  <PropertyGroup>
                    <SharedVersion>1.0.0</SharedVersion>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="../../shared/Shared.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("Hello");
                """),
            ("src/Other/Other.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Other/Dummy.cs", """
                namespace Other;
                public class Dummy { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="src/Other/Other.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the shared .props file
        repo.CreateCommit(
            ("shared/Shared.props", """
                <Project>
                  <PropertyGroup>
                    <SharedVersion>2.0.0</SharedVersion>
                  </PropertyGroup>
                </Project>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--engine", engine,
            "--full-rebuild-trigger", "**/non-existent-trigger.txt");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only App should be affected (it imports Shared.props), Other should NOT
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("RoslynWorkspace")]
    [InlineData("StaticGraph")]
    public async Task RoslynWorkspace_MultipleProjects_OnlyChangedProjectIncluded(string engine)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("global.json", """
                {
                  "msbuild-sdks": {
                    "Microsoft.Build.Traversal": "4.1.82"
                  }
                }
                """),
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Only modify App1
        repo.CreateCommit(
            ("src/App1/Program.cs", """
                Console.WriteLine("App1 modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--engine", engine);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("MSBuild")]
    [InlineData("RoslynWorkspace")]
    [InlineData("StaticGraph")]
    public async Task MeziantouSdk_SingleProject_FileChanged_ProjectIncluded(string engine)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project using Meziantou.NET.Sdk with global.json
        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": {
                    "version": "10.0.100",
                    "allowPrerelease": true,
                    "rollForward": "latestMajor"
                  },
                  "msbuild-sdks": {
                    "Meziantou.NET.Sdk": "1.0.94",
                    "Microsoft.Build.Traversal": "4.1.82"
                  }
                }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Meziantou.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("Hello");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify the source file
        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("Hello, World!");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--engine", engine);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("MSBuild")]
    [InlineData("RoslynWorkspace")]
    [InlineData("StaticGraph")]
    public async Task MeziantouSdk_MultipleProjects_OnlyChangedProjectIncluded(string engine)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects using Meziantou.NET.Sdk
        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": {
                    "version": "10.0.100",
                    "allowPrerelease": true,
                    "rollForward": "latestMajor"
                  },
                  "msbuild-sdks": {
                    "Meziantou.NET.Sdk": "1.0.94",
                    "Microsoft.Build.Traversal": "4.1.82"
                  }
                }
                """),
            ("src/App1/App1.csproj", """
                <Project Sdk="Meziantou.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Meziantou.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App1/App1.csproj" />
                    <ProjectReference Include="src/App2/App2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Only modify App1
        repo.CreateCommit(
            ("src/App1/Program.cs", """
                Console.WriteLine("App1 modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--engine", engine);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Theory]
    [InlineData("MSBuild")]
    [InlineData("RoslynWorkspace")]
    [InlineData("StaticGraph")]
    public async Task MeziantouSdk_TransitiveDependents_AreIncluded(string engine)
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create Lib -> App dependency chain using Meziantou.NET.Sdk
        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": {
                    "version": "10.0.100",
                    "allowPrerelease": true,
                    "rollForward": "latestMajor"
                  },
                  "msbuild-sdks": {
                    "Meziantou.NET.Sdk": "1.0.94",
                    "Microsoft.Build.Traversal": "4.1.82"
                  }
                }
                """),
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Meziantou.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Meziantou.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("src/Unrelated/Unrelated.csproj", """
                <Project Sdk="Meziantou.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Unrelated/Dummy.cs", """
                namespace Unrelated;
                public class Dummy { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                    <ProjectReference Include="src/Lib/Lib.csproj" />
                    <ProjectReference Include="src/Unrelated/Unrelated.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify only the Lib source file
        repo.CreateCommit(
            ("src/Lib/MyClass.cs", """
                namespace Lib;
                public class MyClass { public int Value { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--engine", engine);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Both Lib and App should be affected (App depends on Lib), Unrelated should not
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task HierarchicalTrigger_GlobalJsonInSubfolder_OnlyAffectsProjectsInSameHierarchy()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create projects in different top-level folders
        repo.CreateCommit(
            ("src/global.json", """
                {
                  "sdk": { "version": "10.0.100", "allowPrerelease": true, "rollForward": "latestMajor" }
                }
                """),
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("src/proj2/proj2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj2/Class2.cs", """
                namespace Proj2;
                public class Class2 { }
                """),
            ("tests/proj1.tests/proj1.tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("tests/proj1.tests/Tests.cs", """
                namespace Proj1.Tests;
                public class Tests { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="src/proj2/proj2.csproj" />
                    <ProjectReference Include="tests/proj1.tests/proj1.tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify global.json in src/ (should only affect src/ projects)
        repo.CreateCommit(
            ("src/global.json", """
                {
                  "sdk": { "version": "10.0.200", "allowPrerelease": true, "rollForward": "latestMajor" }
                }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only src/ projects should be affected, NOT tests/proj1.tests
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj2/proj2.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task HierarchicalTrigger_GlobalJsonAtRoot_AffectsAllProjects()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create projects in different top-level folders
        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": { "version": "10.0.100", "allowPrerelease": true, "rollForward": "latestMajor" }
                }
                """),
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("tests/proj1.tests/proj1.tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("tests/proj1.tests/Tests.cs", """
                namespace Proj1.Tests;
                public class Tests { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="tests/proj1.tests/proj1.tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify root global.json (should affect ALL projects)
        repo.CreateCommit(
            ("global.json", """
                {
                  "sdk": { "version": "10.0.200", "allowPrerelease": true, "rollForward": "latestMajor" }
                }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // All projects should be affected because root-level global.json affects everything
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)tests/proj1.tests/proj1.tests.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task HierarchicalTrigger_NuGetConfigInSubfolder_OnlyAffectsProjectsInSameHierarchy()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create projects in different top-level folders
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("tests/proj1.tests/proj1.tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("tests/proj1.tests/Tests.cs", """
                namespace Proj1.Tests;
                public class Tests { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="tests/proj1.tests/proj1.tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Add nuget.config inside src/ (should only affect src/ projects)
        repo.CreateCommit(
            ("src/nuget.config", """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only src/proj1 should be affected, NOT tests/proj1.tests
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task CustomHierarchicalTrigger_ReplacesDefaults()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create projects in different top-level folders
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("tests/proj1.tests/proj1.tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("tests/proj1.tests/Tests.cs", """
                namespace Proj1.Tests;
                public class Tests { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="tests/proj1.tests/proj1.tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Add a custom trigger file in src/
        repo.CreateCommit(
            ("src/build.lock", "locked")
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        // Use custom hierarchical trigger that matches build.lock
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--hierarchical-rebuild-trigger", "**/build.lock");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only src/proj1 should be affected (build.lock is in src/)
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task HierarchicalTrigger_EditorConfigInSubfolder_OnlyAffectsProjectsInSameHierarchy()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create projects in different top-level folders
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("tests/proj1.tests/proj1.tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("tests/proj1.tests/Tests.cs", """
                namespace Proj1.Tests;
                public class Tests { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="tests/proj1.tests/proj1.tests.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Add .editorconfig inside src/ (should only affect src/ projects)
        repo.CreateCommit(
            ("src/.editorconfig", """
                root = true
                [*.cs]
                indent_size = 4
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only src/proj1 should be affected, NOT tests/proj1.tests
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task EditorConfigItem_TrackedAsOwnedFile()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects, one with an .editorconfig next to it
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/.editorconfig", """
                root = true
                [*.cs]
                indent_size = 4
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("src/proj2/proj2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj2/Class2.cs", """
                namespace Proj2;
                public class Class2 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="src/proj2/proj2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Change the .editorconfig next to proj1
        // MSBuild discovers this via EditorConfigFiles item → should be tracked as an owned file of proj1
        // We disable hierarchical triggers so only direct file ownership detection is tested
        repo.CreateCommit(
            ("src/proj1/.editorconfig", """
                root = true
                [*.cs]
                indent_size = 2
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--hierarchical-rebuild-trigger", "nonexistent-pattern-to-disable-defaults");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only proj1 should be affected because its .editorconfig is tracked as an EditorConfigFiles item
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task GlobalEditorConfigItem_TrackedAsOwnedFile()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project with a .globalconfig
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <GlobalAnalyzerConfigFiles Include="custom.globalconfig" />
                  </ItemGroup>
                </Project>
                """),
            ("src/proj1/custom.globalconfig", """
                is_global = true
                dotnet_diagnostic.CA1000.severity = warning
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("src/proj2/proj2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj2/Class2.cs", """
                namespace Proj2;
                public class Class2 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="src/proj2/proj2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Change the .globalconfig
        repo.CreateCommit(
            ("src/proj1/custom.globalconfig", """
                is_global = true
                dotnet_diagnostic.CA1000.severity = error
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--hierarchical-rebuild-trigger", "nonexistent-pattern-to-disable-defaults");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only proj1 should be affected because its .globalconfig is tracked as a GlobalAnalyzerConfigFiles item
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task WorkingTree_ModifiedFile_DetectsAffectedProject()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("src/proj2/proj2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj2/Class2.cs", """
                namespace Proj2;
                public class Class2 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="src/proj2/proj2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Modify a file without committing (working tree change)
        repo.WriteFiles(
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { public void NewMethod() { } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^1],
            "--working-tree");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only proj1 should be affected because only its source file was modified
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj1/proj1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task WorkingTree_UntrackedFile_DetectsAffectedProject()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("src/proj2/proj2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj2/Class2.cs", """
                namespace Proj2;
                public class Class2 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                    <ProjectReference Include="src/proj2/proj2.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Add a new untracked file to proj2 (not staged, not committed)
        repo.WriteFiles(
            ("src/proj2/NewClass.cs", """
                namespace Proj2;
                public class NewClass { }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^1],
            "--working-tree");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Only proj2 should be affected because the new untracked file is in its directory
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/proj2/proj2.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task WorkingTree_NoChanges_ProducesEmptyOutput()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create a project
        repo.CreateCommit(
            ("src/proj1/proj1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/proj1/Class1.cs", """
                namespace Proj1;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/proj1/proj1.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // No working tree modifications — compare HEAD against working tree
        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        var stdout = await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^1],
            "--working-tree");

        Assert.Contains("No changed files detected. Skipping project graph analysis.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Analyzing projects using engine:", stdout, StringComparison.Ordinal);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // No projects should be affected
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup />
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task PullRequestEnvironment_UsesGitHubBaseRefForMergeBase()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("main");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        var mergeBaseCommit = repo.Commits[^1];

        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("pr branch");
                """)
        );
        repo.SetRemoteTrackingBranch("main", mergeBaseCommit)
            .SetDefaultRemoteBranch("main");

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        var stdout = await RunTool(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GITHUB_ACTIONS"] = "true",
                ["GITHUB_EVENT_NAME"] = "pull_request",
                ["GITHUB_BASE_REF"] = "main",
            },
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath);

        Assert.Contains("Detected GitHub Actions pull request context, using base branch: origin/main", stdout, StringComparison.Ordinal);
        Assert.Contains($"Comparing {mergeBaseCommit} -> {repo.Commits[^1]}", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullRequestEnvironment_IgnoresBaseRefOutsidePullRequestEvents()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("main");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/App/Program.cs", """
                Console.WriteLine("push");
                """)
        );
        repo.SetRemoteTrackingBranch("main", repo.Commits[^2])
            .SetDefaultRemoteBranch("main");

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        var stdout = await RunTool(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GITHUB_ACTIONS"] = "true",
                ["GITHUB_EVENT_NAME"] = "push",
                ["GITHUB_BASE_REF"] = "main",
            },
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath);

        Assert.Contains("Auto-detected base branch: origin/main", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Detected GitHub Actions pull request context", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TraversalWithGlobbing_ProjectsDiscoveredAndIncluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create two projects and a traversal that uses a glob pattern
        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/**/*.*proj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Only modify App1
        repo.CreateCommit(
            ("src/App1/Program.cs", """
                Console.WriteLine("App1 modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task TraversalWithIncludeAndRemove_RemovedProjectExcluded()
    {
        var repo = await CreateRepositoryAsync();

        // Commit 1: Create three projects and a traversal that includes all then removes one
        repo.CreateCommit(
            ("src/App1/App1.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App1/Program.cs", """
                Console.WriteLine("App1");
                """),
            ("src/App2/App2.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2");
                """),
            ("src/Excluded/Excluded.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Excluded/Program.cs", """
                Console.WriteLine("Excluded");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/**/*.*proj" />
                    <ProjectReference Remove="src/Excluded/Excluded.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        // Commit 2: Modify all three projects
        repo.CreateCommit(
            ("src/App1/Program.cs", """
                Console.WriteLine("App1 modified");
                """),
            ("src/App2/Program.cs", """
                Console.WriteLine("App2 modified");
                """),
            ("src/Excluded/Program.cs", """
                Console.WriteLine("Excluded modified");
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1]);

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        // Excluded.csproj should NOT appear because it was removed from ProjectReference
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App1/App1.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App2/App2.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task ProjectBundle_ChangedProjectIncludesBundledProject()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/B/B.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/B/Class1.cs", """
                namespace B;
                public class Class1 { }
                """),
            ("src/C/C.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/C/Class1.cs", """
                namespace C;
                public class Class1 { }
                """),
            ("src/U/U.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/U/Class1.cs", """
                namespace U;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/B/B.csproj" />
                    <ProjectReference Include="src/C/C.csproj" />
                    <ProjectReference Include="src/U/U.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/B/Class1.cs", """
                namespace B;
                public class Class1 { public int Value { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--project-bundle", "src/B/B.csproj,src/C/C.csproj");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/B/B.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/C/C.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task ProjectBundle_IsSymmetric()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/B/B.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/B/Class1.cs", """
                namespace B;
                public class Class1 { }
                """),
            ("src/C/C.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/C/Class1.cs", """
                namespace C;
                public class Class1 { }
                """),
            ("src/U/U.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/U/Class1.cs", """
                namespace U;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/B/B.csproj" />
                    <ProjectReference Include="src/C/C.csproj" />
                    <ProjectReference Include="src/U/U.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/C/Class1.cs", """
                namespace C;
                public class Class1 { public int Value { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--project-bundle", "src/B/B.csproj,src/C/C.csproj");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/B/B.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/C/C.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task ProjectBundle_AddedProjectPropagatesToDependents()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/B/B.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/B/Class1.cs", """
                namespace B;
                public class Class1 { }
                """),
            ("src/C/C.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/C/Class1.cs", """
                namespace C;
                public class Class1 { }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../B/B.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                Console.WriteLine("App");
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/B/B.csproj" />
                    <ProjectReference Include="src/C/C.csproj" />
                    <ProjectReference Include="src/App/App.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/C/Class1.cs", """
                namespace C;
                public class Class1 { public int Value { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        await RunTool(
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--project-bundle", "src/B/B.csproj,src/C/C.csproj");

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        InlineSnapshot.Validate(content.Trim(), """
            <Project Sdk="Microsoft.Build.Traversal">
              <Import Project="$(MSBuildThisFileDirectory)output.before.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/App/App.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/B/B.csproj" />
                <ProjectReference Include="$(MSBuildThisFileDirectory)src/C/C.csproj" />
              </ItemGroup>
              <Import Project="$(MSBuildThisFileDirectory)output.after.proj" Condition="Exists('$(MSBuildThisFileDirectory)output.after.proj')" />
            </Project>
            """);
    }

    [Fact]
    public async Task ProjectBundle_InvalidProjectPath_FailsWithClearError()
    {
        var repo = await CreateRepositoryAsync();

        repo.CreateCommit(
            ("src/B/B.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/B/Class1.cs", """
                namespace B;
                public class Class1 { }
                """),
            ("src/C/C.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/C/Class1.cs", """
                namespace C;
                public class Class1 { }
                """),
            ("dirs.proj", """
                <Project Sdk="Microsoft.Build.Traversal">
                  <ItemGroup>
                    <ProjectReference Include="src/B/B.csproj" />
                    <ProjectReference Include="src/C/C.csproj" />
                  </ItemGroup>
                </Project>
                """)
        );

        repo.CreateCommit(
            ("src/B/Class1.cs", """
                namespace B;
                public class Class1 { public int Value { get; set; } }
                """)
        );

        var outputPath = Path.Combine(repo.RepositoryPath, "output.proj");
        var result = await ToolRunner.RunToolRawAsync(
            output,
            "generate",
            "--input", Path.Combine(repo.RepositoryPath, "dirs.proj"),
            "--output", outputPath,
            "--repository", repo.RepositoryPath,
            "--base-commit", repo.Commits[^2],
            "--head-commit", repo.Commits[^1],
            "--project-bundle", "src/B/B.csproj,src/Missing/Missing.csproj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not present in the input project set", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }
}
