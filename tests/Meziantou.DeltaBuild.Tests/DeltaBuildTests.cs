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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App1/App1.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
                <ProjectReference Include="src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App1/App1.csproj" />
                <ProjectReference Include="src/App2/App2.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/Feature1/App1.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup />
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
            </Project>
            """);
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/FSharpLib/FSharpLib.fsproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
                <ProjectReference Include="src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/A/A.csproj" />
                <ProjectReference Include="src/B/B.csproj" />
                <ProjectReference Include="src/C/C.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
                <ProjectReference Include="src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/Lib/Lib.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
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
              <Import Project="output.before.proj" Condition="Exists('output.before.proj')" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
              </ItemGroup>
              <Import Project="output.after.proj" Condition="Exists('output.after.proj')" />
            </Project>
            """);
    }
}
