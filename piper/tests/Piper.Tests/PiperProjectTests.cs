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
            Assert.Contains("isn't one that comes with piper (actors, args, parallel, remote-actors, services, web)", error.Message);
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
}
