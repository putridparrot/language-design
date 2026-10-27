using System.Diagnostics;

namespace Piper.Tests;

/// <summary><c>piper build --exe/--library</c>: compiled programs written to disk, and run as ordinary .NET programs.</summary>
public sealed class AssemblyOutputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "piper-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static CompilationResult Compile(string program)
    {
        var result = Compilation.Compile(program, "app.piper", new CompilationOptions(LineDirectives: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        return result;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments = "")
    {
        var start = new ProcessStartInfo(fileName, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void An_executable_runs_on_its_own()
    {
        var result = Compile("""
            fn Main() {
                let (q, r) = (17 / 5, 17 % 5);
                Print($"{q} r {r}");
            }
            """);

        var output = AssemblyWriter.Write(result.Files, _directory, "app", executable: true);

        Assert.True(output.Success, string.Join("\n", output.Errors));
        foreach (var file in new[] { "app.dll", "app.pdb", "app.xml", "app.runtimeconfig.json", "Piper.Runtime.dll" })
            Assert.True(File.Exists(Path.Combine(_directory, file)), file);

        // Through `dotnet app.dll`, and through the native launcher where the SDK provides one.
        Assert.Equal((0, "3 r 2\n"), Run("dotnet", $"\"{Path.Combine(_directory, "app.dll")}\""));
        if (output.Launcher is { } launcher)
            Assert.Equal((0, "3 r 2\n"), Run(launcher));
    }

    [Fact]
    public void Stack_traces_of_a_built_executable_show_piper_lines()
    {
        var result = Compile("""
            fn fail() => int {
                throw System.InvalidOperationException("oops")
            }

            fn Main() {
                Print(fail());
            }
            """);

        var output = AssemblyWriter.Write(result.Files, _directory, "app", executable: true);
        var (exitCode, text) = Run("dotnet", $"\"{Path.Combine(_directory, "app.dll")}\"");

        Assert.True(output.Success);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("app.piper:line 2", text);
        Assert.Contains("app.piper:line 6", text);
    }

    [Fact]
    public void The_pdb_maps_statements_to_piper_lines_for_debuggers()
    {
        // Debuggers bind a breakpoint by finding the PDB document with the breakpoint's file path and a sequence point
        // on its line.
        var path = Path.Combine(_directory, "src", "app.piper");
        var source = """
            fn twice(x: int) => int {
                let y = x * 2;
                y
            }

            fn Main() {
                Print(twice(21));
            }
            """;
        var result = Compilation.Compile([new Piper.Text.SourceText(source, path)], new CompilationOptions(LineDirectives: true));
        Assert.True(result.Success);

        var output = AssemblyWriter.Write(result.Files, _directory, "app", executable: true);
        Assert.True(output.Success);

        using var stream = File.OpenRead(Path.Combine(_directory, "app.pdb"));
        using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(stream);
        var pdb = provider.GetMetadataReader();
        var lines = pdb.MethodDebugInformation
            .SelectMany(handle => pdb.GetMethodDebugInformation(handle).GetSequencePoints())
            .Where(point => !point.IsHidden && pdb.GetString(pdb.GetDocument(point.Document).Name) == path)
            .Select(point => point.StartLine)
            .ToHashSet();
        Assert.Superset(new HashSet<int> { 2, 3, 7 }, lines);
    }

    [Fact]
    public void A_library_has_no_runtime_config_or_launcher()
    {
        var result = Compile("fn square(x: int) => int = x * x;");

        var output = AssemblyWriter.Write(result.Files, _directory, "maths", executable: false);

        Assert.True(output.Success);
        Assert.Null(output.Launcher);
        Assert.True(File.Exists(Path.Combine(_directory, "maths.dll")));
        Assert.False(File.Exists(Path.Combine(_directory, "maths.runtimeconfig.json")));
        Assert.False(result.HasMain);
    }

    /// <summary>A library exports what's declared `public`; the rest is visible only to the library itself.</summary>
    [Fact]
    public void A_library_exports_only_its_public_declarations()
    {
        var result = Compile("""
            namespace Shapes;

            using System;

            public record Point(x: double, y: double);
            record Cache(hits: int);
            public trait Shape { fn Area() => double; }
            trait Helper { fn Help() => int; }
            public class Circle(radius: double) { fn Area() => double = Square(radius) * 3.14; }
            public enum Size { Small, Large }

            public fn Distance(a: Point, b: Point) => double = Math.Sqrt(Square(a.x - b.x) + Square(a.y - b.y));
            fn Square(x: double) => double = x * x;
            private fn Cube(x: double) => double = x * x * x;

            public extend double { fn Doubled() => double = this * 2.0; }
            extend int { fn Tripled() => int = this * 3; }
            """);
        var output = AssemblyWriter.Write(result.Files, _directory, "shapes", executable: false);
        Assert.True(output.Success, string.Join("\n", output.Errors));

        // Loaded on its own, and from memory, so the file isn't locked when the test's folder is deleted.
        var context = new System.Runtime.Loader.AssemblyLoadContext("exports", isCollectible: true);
        try
        {
            using var image = new MemoryStream(File.ReadAllBytes(Path.Combine(_directory, "shapes.dll")));
            var assembly = context.LoadFromStream(image);
            // The types, the file's module class (for Distance) and its extensions class (for Doubled).
            var exported = assembly.GetExportedTypes().Where(t => !t.IsNested).Select(t => t.Name).Order().ToArray();
            Assert.Equal(["Circle", "Point", "Shape", "Size"], exported.Where(n => !n.StartsWith("App", StringComparison.Ordinal)).ToArray());
            Assert.Equal(2, exported.Count(n => n.StartsWith("App", StringComparison.Ordinal)));

            // The module class holds Distance (public) and Square (internal); the extensions class Doubled only.
            var functions = assembly.GetExportedTypes().SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                .Select(m => m.Name).ToHashSet();
            Assert.Contains("Distance", functions);
            Assert.DoesNotContain("Square", functions);
            Assert.DoesNotContain("Cube", functions);
            Assert.DoesNotContain("Tripled", functions);
        }
        finally
        {
            context.Unload();
        }
    }
}
