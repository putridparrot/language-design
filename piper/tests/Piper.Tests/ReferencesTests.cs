using System.IO.Compression;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// piper.json's packages, frameworks and references: resolved, bound against, run and written. Offline: the package
/// comes from a feed in a temporary folder, and nothing is added to the machine's NuGet cache.
/// </summary>
public sealed class ReferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "piper-references-" + Guid.NewGuid().ToString("N"));

    public ReferencesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A .NET library, <c>Greetings.dll</c>, with <c>Greetings.Greeter.Hello(name)</c>, compiled from C#.</summary>
    private static byte[] GreetingsLibrary(string greeting = "Hello")
    {
        var build = Runner.Build(
            [new GeneratedFile("Greeter.cs", $$"""namespace Greetings; public static class Greeter { public static string Hello(string name) => $"{{greeting}} {name}"; }""")],
            "Greetings");
        Assert.True(build.Success, string.Join("\n", build.Errors));
        return build.Assembly!;
    }

    /// <summary>Writes a program folder with <paramref name="json"/> as its piper.json and <paramref name="main"/> as main.piper.</summary>
    private string Program(string json, string main)
    {
        var folder = Path.Combine(_directory, "app");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PiperProject.FileName), json);
        File.WriteAllText(Path.Combine(folder, "main.piper"), main);
        return folder;
    }

    private static string Run(string folder)
    {
        var (project, errors) = LoadedProject.Load(folder);
        Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.Format())));
        var source = new SourceText(File.ReadAllText(Path.Combine(folder, "main.piper")), "main.piper");
        var result = Compilation.Compile([source], new CompilationOptions { References = project!.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }

    private const string UsesGreetings = """
        using Greetings;
        fn Main() { Print(Greeter.Hello("Ada")); }
        """;

    [Fact]
    public void A_referenced_assembly_can_be_used_run_and_written()
    {
        var folder = Program("""{ "references": ["lib/Greetings.dll"] }""", UsesGreetings);
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        File.WriteAllBytes(Path.Combine(folder, "lib", "Greetings.dll"), GreetingsLibrary());

        Assert.Equal("Hello Ada\n", Run(folder));

        var (project, _) = LoadedProject.Load(folder);
        var source = new SourceText(File.ReadAllText(Path.Combine(folder, "main.piper")), "main.piper");
        var result = Compilation.Compile([source], new CompilationOptions { References = project!.References });
        var output = AssemblyWriter.Write(result.Files, Path.Combine(folder, "bin"), "app", executable: true, result.References);
        Assert.True(output.Success, string.Join("\n", output.Errors));
        Assert.True(File.Exists(Path.Combine(folder, "bin", "Greetings.dll")));
    }

    [Fact]
    public void Without_the_reference_its_types_are_unknown()
    {
        var result = Compilation.Compile(UsesGreetings);
        Assert.Contains(result.Diagnostics, d => d.Code == "PIP0272");
    }

    [Fact]
    public void A_shared_framework_can_be_used_and_is_named_in_runtimeconfig()
    {
        var folder = Program("""{ "frameworks": ["Microsoft.AspNetCore.App"] }""", """
            using Microsoft.Extensions.DependencyInjection;
            record Clock(time: string);
            fn Main() {
                let services = ServiceCollection();
                services.AddSingleton<Clock>(Clock("noon"));
                Print(services.BuildServiceProvider().GetRequiredService<Clock>().time);
            }
            """);
        Assert.Equal("noon\n", Run(folder));

        var (project, _) = LoadedProject.Load(folder);
        var source = new SourceText(File.ReadAllText(Path.Combine(folder, "main.piper")), "main.piper");
        var result = Compilation.Compile([source], new CompilationOptions { References = project!.References });
        var output = AssemblyWriter.Write(result.Files, Path.Combine(folder, "bin"), "app", executable: true, result.References);
        Assert.True(output.Success, string.Join("\n", output.Errors));
        Assert.Contains("Microsoft.AspNetCore.App", File.ReadAllText(Path.Combine(folder, "bin", "app.runtimeconfig.json")));
        // A framework's assemblies come with .NET, so they aren't copied.
        Assert.False(File.Exists(Path.Combine(folder, "bin", "Microsoft.Extensions.DependencyInjection.dll")));
    }

    [Fact]
    public void A_package_is_restored_used_and_written()
    {
        var folder = Program("""{ "packages": { "Piper.Test.Greetings": "1.0.0" } }""", UsesGreetings);
        LocalFeed(folder, ("Piper.Test.Greetings", "1.0.0", GreetingsLibrary()));

        Assert.Equal("Hello Ada\n", Run(folder));
        Assert.True(File.Exists(Path.Combine(folder, "obj", "piper", "references.json")));

        var (project, _) = LoadedProject.Load(folder);
        var source = new SourceText(File.ReadAllText(Path.Combine(folder, "main.piper")), "main.piper");
        var result = Compilation.Compile([source], new CompilationOptions { References = project!.References });
        var output = AssemblyWriter.Write(result.Files, Path.Combine(folder, "bin"), "app", executable: true, result.References);
        Assert.True(output.Success, string.Join("\n", output.Errors));
        Assert.True(File.Exists(Path.Combine(folder, "bin", "Greetings.dll")));
    }

    [Fact]
    public void A_package_that_cant_be_restored_is_reported()
    {
        var folder = Program("""{ "packages": { "Piper.Test.Missing": "1.0.0" } }""", UsesGreetings);
        LocalFeed(folder);
        var (project, errors) = LoadedProject.Load(folder);
        Assert.Null(project);
        var error = Assert.Single(errors);
        Assert.Equal("PIP0401", error.Code);
        Assert.Contains("Piper.Test.Missing", error.Message);
    }

    [Fact]
    public void A_plugins_frameworks_are_the_programs_too()
    {
        // `services name;` becomes `let name = ServiceCollection();`, which needs ASP.NET Core's DI, so the plugin says so.
        var plugin = Runner.Build([new GeneratedFile("ServicesPlugin.cs", """
            using System.Collections.Generic;
            using Piper.Plugins;
            using Piper.Syntax;
            public sealed class ServicesPlugin : IPiperPlugin
            {
                public string Name => "services";
                public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];
                public IReadOnlyList<PluginKeyword> Keywords { get; } =
                [
                    new StatementKeyword("services", p =>
                    {
                        var name = p.Expect(TokenKind.Identifier);
                        p.Expect(TokenKind.Semicolon);
                        return [p.Template.Statement("let $name = Microsoft.Extensions.DependencyInjection.ServiceCollection();", ("name", name))];
                    }),
                ];
            }
            """)], "ServicesPlugin");
        Assert.True(plugin.Success, string.Join("\n", plugin.Errors));

        var folder = Program("""{ "plugins": ["ServicesPlugin.dll"] }""", """
            fn Main() {
                services registry;
                Print(registry.Count);
            }
            """);
        File.WriteAllBytes(Path.Combine(folder, "ServicesPlugin.dll"), plugin.Assembly!);

        var (project, errors) = LoadedProject.Load(folder);
        Assert.Empty(errors);
        Assert.Equal("Microsoft.AspNetCore.App", Assert.Single(project!.References.Frameworks).Name);
        var source = new SourceText(File.ReadAllText(Path.Combine(folder, "main.piper")), "main.piper");
        var result = Compilation.Compile([source], new CompilationOptions { Plugins = project.Plugins, References = project.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("0", Runner.Run(result.Files, references: result.References).Output.Trim());
    }

    [Theory]
    [InlineData("""{ "frameworks": ["Nope.App"] }""", "PIP0402")]
    [InlineData("""{ "references": ["missing.dll"] }""", "PIP0403")]
    [InlineData("""{ "references": ["main.piper"] }""", "PIP0403")]
    public void Unresolvable_references_are_reported(string json, string code)
    {
        var folder = Program(json, "fn Main() {}");
        var (project, errors) = LoadedProject.Load(folder);
        Assert.Null(project);
        Assert.Equal(code, Assert.Single(errors).Code);
    }

    /// <summary>
    /// A NuGet feed in a temporary folder holding <paramref name="packages"/>, which is the only one <paramref name="folder"/>'s
    /// restores use (through a nuget.config there), with a packages folder of its own.
    /// </summary>
    private void LocalFeed(string folder, params (string Id, string Version, byte[] Assembly)[] packages)
    {
        var feed = Path.Combine(_directory, "feed");
        Directory.CreateDirectory(feed);
        foreach (var (id, version, assembly) in packages)
        {
            using var zip = ZipFile.Open(Path.Combine(feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open()))
            {
                writer.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata><id>{id}</id><version>{version}</version><authors>Piper tests</authors><description>A test package.</description></metadata>
                    </package>
                    """);
            }
            using var stream = zip.CreateEntry($"lib/net{Environment.Version.Major}.{Environment.Version.Minor}/Greetings.dll").Open();
            stream.Write(assembly);
        }

        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="{Path.Combine(_directory, "packages")}" /></config>
              <packageSources><clear /><add key="test" value="{feed}" /></packageSources>
            </configuration>
            """);
    }
}
