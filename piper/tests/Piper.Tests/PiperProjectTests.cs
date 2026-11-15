using Piper.Text;

namespace Piper.Tests;

/// <summary>piper.json: what a program folder is built into.</summary>
public class PiperProjectTests
{
    private static (PiperProject? Project, System.Collections.Immutable.ImmutableArray<Diagnostics.Diagnostic> Errors) Parse(string text) =>
        PiperProject.Parse(new SourceText(text, "piper.json"));

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("{}")]
    public void An_empty_file_or_object_is_the_default(string text)
    {
        var (project, errors) = Parse(text);
        Assert.Empty(errors);
        Assert.Equal(PiperProject.Empty, project);
    }

    [Fact]
    public void Reads_name_and_output_allowing_comments_and_trailing_commas()
    {
        var (project, errors) = Parse("""
            {
              // what to build
              "name": "greeter",
              "output": "library",
            }
            """);
        Assert.Empty(errors);
        Assert.Equal(new PiperProject("greeter", PiperOutput.Library), project);
    }

    [Theory]
    [InlineData("{ \"sources\": {} }", "Unknown key \"sources\"")]
    [InlineData("{ \"packages\": [\"Humanizer\"] }", "\"packages\" must map NuGet package names to versions")]
    [InlineData("{ \"packages\": { \"Humanizer\": 2 } }", "\"packages\" must map NuGet package names to versions")]
    [InlineData("{ \"frameworks\": \"Microsoft.AspNetCore.App\" }", "\"frameworks\" must be a list")]
    [InlineData("{ \"references\": [\"\"] }", "\"references\" must be a list")]
    [InlineData("{ \"output\": \"dll\" }", "\"output\" must be \"exe\" or \"library\"")]
    [InlineData("{ \"name\": \"\" }", "\"name\" must be a non-empty string")]
    [InlineData("{ \"name\": \"a/b\" }", "\"name\" must be a non-empty string")]
    [InlineData("{ \"name\": 3 }", "\"name\" must be a non-empty string")]
    [InlineData("[]", "must hold a JSON object")]
    [InlineData("{ \"name\": ", "isn't valid JSON")]
    [InlineData("{ \"plugins\": \"a.dll\" }", "\"plugins\" must be a list of plugins")]
    [InlineData("{ \"plugins\": [\"\"] }", "\"plugins\" must be a list of plugins")]
    [InlineData("{ \"plugins\": [1] }", "\"plugins\" must be a list of plugins")]
    public void Anything_else_is_an_error(string text, string message)
    {
        var (project, errors) = Parse(text);
        Assert.Null(project);
        var error = Assert.Single(errors);
        Assert.Equal("PIP0400", error.Code);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Reads_plugin_paths()
    {
        var (project, errors) = Parse("""{ "plugins": ["../plugins/A.dll", "B.dll"] }""");
        Assert.Empty(errors);
        Assert.Equal(["../plugins/A.dll", "B.dll"], project!.Plugins.ToArray());
    }

    [Fact]
    public void Reads_packages_frameworks_and_references()
    {
        var (project, errors) = Parse("""
            {
              "packages": { "Humanizer.Core": "2.14.1" },
              "frameworks": ["Microsoft.AspNetCore.App"],
              "references": ["lib/My.dll"],
            }
            """);
        Assert.Empty(errors);
        Assert.Equal("2.14.1", project!.Packages["humanizer.core"]);
        Assert.Equal(["Microsoft.AspNetCore.App"], project.Frameworks.ToArray());
        Assert.Equal(["lib/My.dll"], project.References.ToArray());
    }

    [Fact]
    public void Errors_point_at_the_key()
    {
        var (_, errors) = Parse("{\n  \"name\": \"app\",\n  \"outptu\": \"exe\"\n}");
        var error = Assert.Single(errors);
        Assert.StartsWith("piper.json(3,3): error PIP0400: Unknown key \"outptu\"", error.Format());
        Assert.Equal("\"outptu\"".Length, error.Span.Length);
    }

    [Fact]
    public void Invalid_json_is_reported_where_it_goes_wrong()
    {
        var (_, errors) = Parse("{\n  \"name\": \"app\"\n  \"output\": \"exe\"\n}");
        Assert.StartsWith("piper.json(3,", Assert.Single(errors).Format());
    }

    [Fact]
    public void Plugins_that_come_with_piper_are_named()
    {
        var folder = Directory.CreateTempSubdirectory("piper-plugins").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, PiperProject.FileName), """{ "plugins": ["services", "web"] }""");
            var (project, errors) = LoadedProject.Load(folder);
            Assert.Empty(errors);
            Assert.Equal(2, project!.Plugins.Length);

            File.WriteAllText(Path.Combine(folder, PiperProject.FileName), """{ "plugins": ["webb"] }""");
            (_, errors) = LoadedProject.Load(folder);
            var error = Assert.Single(errors);
            Assert.Contains("isn't one that comes with piper (actors, args, aspects, config, data, openapi, parallel, remote-actors, services, state-machines, web)", error.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// In a folder that isn't trusted, only what can't run the folder's code is loaded: named plugins, not those given
    /// by path, packages (restoring runs MSBuild there) or assemblies. What's left out is a warning, not an error, so
    /// the named plugins still work.
    /// </summary>
    [Fact]
    public void A_folder_that_isnt_trusted_loads_only_plugins_by_name()
    {
        var folder = Directory.CreateTempSubdirectory("piper-untrusted").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, PiperProject.FileName), """
                {
                  "plugins": ["parallel", "tools/Evil.Plugin.dll"],
                  "packages": { "Humanizer": "2.14.1" },
                  "references": ["lib/Evil.dll"]
                }
                """);
            var (project, errors) = LoadedProject.Load(folder, trusted: false);
            Assert.Empty(errors);
            Assert.Equal("parallel", Assert.Single(project!.Plugins).Name);
            Assert.True(project.References.Assemblies.IsEmpty);
            var warning = Assert.Single(project.Warnings);
            Assert.Equal(("PIP0404", Piper.Diagnostics.DiagnosticSeverity.Warning), (warning.Code, warning.Severity));
            Assert.Contains("the plugin 'tools/Evil.Plugin.dll', the package 'Humanizer', the assembly 'lib/Evil.dll'", warning.Message);
            Assert.False(Directory.Exists(Path.Combine(folder, "obj")));    // nothing was restored

            // Trusted, the same folder's plugin path is loaded (and is missing).
            (_, errors) = LoadedProject.Load(folder);
            Assert.Contains("Evil.Plugin.dll", Assert.Single(errors).Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>What `piper new` makes compiles without warnings, and its tests pass; the console one runs.</summary>
    [Theory]
    [InlineData("console")]
    [InlineData("web")]
    public void Templates_make_working_programs(string template)
    {
        var folder = Directory.CreateTempSubdirectory("piper-new").FullName;
        try
        {
            var files = ProjectTemplates.Files(template, "sample");
            Assert.Equal([".gitignore", "main.piper", PiperProject.FileName], files.Select(f => f.Path).ToArray());
            foreach (var (path, text) in files)
                File.WriteAllText(Path.Combine(folder, path), text);

            var (project, errors) = LoadedProject.Load(folder);
            Assert.Empty(errors);
            Assert.Equal("sample", project!.Project.Name);
            var sources = files.Where(f => f.Path.EndsWith(".piper")).Select(f => new SourceText(f.Text, Path.Combine(folder, f.Path))).ToList();
            var options = new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = project.Plugins, References = project.References };
            var result = Compilation.Compile(sources, options);
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            Assert.Empty(result.Diagnostics);
            var tests = Runner.RunTests(result.Files, references: result.References);
            Assert.True(tests.Success && tests.Results.All(r => r.Passed), string.Join("\n", tests.Errors));

            if (template == "console")
            {
                var run = Runner.Run(Compilation.Compile(sources, options with { IncludeTests = false }).Files, args: ["Ada"]);
                Assert.Equal("Hello, Ada!", run.Output.Trim());
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("hello", true)]
    [InlineData("todo-api", true)]
    [InlineData("My.App_2", true)]
    [InlineData("9lives", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void Program_names_are_checked(string name, bool valid) =>
        Assert.Equal(valid, ProjectTemplates.IsValidName(name));

    [Theory]
    [InlineData("Piper.Plugins.StateMachines", "StateMachines", "state-machines")]
    [InlineData("my-plugin", "MyPlugin", "my-plugin")]
    [InlineData("greet", "Greet", "greet")]
    [InlineData("9lives", null, null)]
    [InlineData("---", null, null)]
    public void Plugin_names_come_from_the_folders(string folder, string? pascal, string? name) =>
        Assert.Equal(pascal is null ? null : (pascal, name!), ProjectTemplates.PluginNames(folder));

    /// <summary>
    /// What `piper plugin new` makes: a project whose plugin compiles against the compiler, and an example program it
    /// runs. The plugin's C# is compiled here rather than with `dotnet build`, which would be slow.
    /// </summary>
    [Fact]
    public void A_new_plugin_compiles_and_runs_its_example()
    {
        var files = ProjectTemplates.PluginFiles("Greet", "greet", "<Reference Include=\"Piper.Compiler\" />").ToDictionary(f => f.Path, f => f.Text);
        Assert.Equal([".gitignore", "GreetPlugin.cs", "Piper.Plugins.Greet.csproj", "example/main.piper", "example/piper.json"], files.Keys.Order(StringComparer.Ordinal));
        Assert.All(files.Values, text => Assert.DoesNotContain("__PASCAL__", text));
        Assert.Contains("<PiperPluginName>greet</PiperPluginName>", files["Piper.Plugins.Greet.csproj"]);
        Assert.Contains("<Reference Include=\"Piper.Compiler\" />", files["Piper.Plugins.Greet.csproj"]);
        Assert.Contains("\"plugins\": [\"greet\"]", files["example/piper.json"]);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Piper.Plugins.IPiperPlugin).Assembly.Location)
            .Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        var usings = "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\n";
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Piper.Plugins.Greet",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(usings), Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(files["GreetPlugin.cs"])],
            references, new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        Assert.DoesNotContain(emitted.Diagnostics, d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);

        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var plugin = (Piper.Plugins.IPiperPlugin)Activator.CreateInstance(assembly.GetType("Piper.Plugins.Greet.GreetPlugin")!)!;
        Assert.Equal("greet", plugin.Name);
        var result = Compilation.Compile([new SourceText(files["example/main.piper"], "main.piper")], new CompilationOptions { Plugins = [plugin] });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files);
        Assert.Equal(string.Concat(Enumerable.Repeat("Hello from the greet plugin\n", 3)), run.Output.Replace("\r\n", "\n"));
    }
}
