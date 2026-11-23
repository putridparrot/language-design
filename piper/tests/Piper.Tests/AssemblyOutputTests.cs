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

    /// <summary>
    /// A WPF program: its types come from the Windows Desktop framework (whose WindowsBase is a later one than the
    /// core runtime's stand-in), its <c>[STAThread]</c> Main runs in a single-threaded apartment (as a window needs),
    /// under <c>piper run</c> and as an executable, and a windowed one's launcher is a GUI program, so it opens no
    /// console. Only on Windows, with the desktop runtime installed.
    /// </summary>
    [Fact]
    public void A_WPF_program_runs_in_a_single_threaded_apartment_without_a_console()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (references, errors) = ProgramReferences.Resolve(Path.GetTempPath(), new Dictionary<string, string>(), ["Microsoft.WindowsDesktop.App"], []);
        if (!errors.IsEmpty)
            return;
        var result = Compilation.Compile("""
            using System;
            using System.Threading;
            using System.Windows;

            [STAThread]
            fn Main() {
                let window = Window();
                window.Title = "Piper";
                Print($"{Thread.CurrentThread.GetApartmentState()} {window.Title}");
            }
            """, "app.piper", new CompilationOptions { References = references });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Matches(@"\[global::System\.STAThread(Attribute)?\]\s*public static void Main\(string\[\] args\)", result.CSharp!);

        var run = Runner.Run(result.Files, references: references);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("STA Piper\n", run.Output.ReplaceLineEndings("\n"));

        var output = AssemblyWriter.Write(result.Files, _directory, "app", executable: true, references, windowed: true);
        Assert.True(output.Success, string.Join("\n", output.Errors));
        if (output.Launcher is not { } launcher)
            return;
        var bytes = File.ReadAllBytes(launcher);
        var pe = BitConverter.ToInt32(bytes, 0x3C);
        Assert.Equal(2, BitConverter.ToUInt16(bytes, pe + 4 + 20 + 68));     // IMAGE_SUBSYSTEM_WINDOWS_GUI
        Assert.Equal((0, "STA Piper\n"), Run(launcher));
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

    /// <summary>
    /// <c>piper build --publish</c>: the executable without a native launcher, and a Dockerfile on .NET's runtime image
    /// (aspnet for ASP.NET Core) of this version, running <c>dotnet app.dll</c>. (Building the image needs Docker; the
    /// output runs with <c>dotnet</c> here.)
    /// </summary>
    [Fact]
    public void A_published_program_is_ready_for_a_container_image()
    {
        var result = Compile("fn Main(args: string[]) { Print($\"hello {args.Length}\"); }");
        var output = AssemblyWriter.Publish(result.Files, _directory, "app", ProgramReferences.None);

        Assert.True(output.Success, string.Join("\n", output.Errors));
        Assert.Null(output.Launcher);
        Assert.False(File.Exists(Path.Combine(_directory, OperatingSystem.IsWindows() ? "app.exe" : "app")));
        var dockerfile = File.ReadAllText(Path.Combine(_directory, "Dockerfile"));
        var version = $"{Environment.Version.Major}.{Environment.Version.Minor}";
        Assert.Contains($"FROM mcr.microsoft.com/dotnet/runtime:{version}\n", dockerfile);
        Assert.Contains("COPY . .\n", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"app.dll\"]\n", dockerfile);
        Assert.Contains(Path.Combine(_directory, "Dockerfile"), output.Files);
        Assert.Equal((0, "hello 2\n"), Run("dotnet", $"\"{Path.Combine(_directory, "app.dll")}\" a b"));
    }

    [Fact]
    public void A_published_web_program_is_on_the_aspnet_image_and_a_desktop_one_is_refused()
    {
        var result = Compile("fn Main() { }");
        var aspnet = ProgramReferences.None with { Frameworks = [new FrameworkReference("Microsoft.AspNetCore.App", "10.0.0", "")] };
        Assert.True(AssemblyWriter.Publish(result.Files, _directory, "web", aspnet).Success);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:", File.ReadAllText(Path.Combine(_directory, "Dockerfile")));

        var desktop = ProgramReferences.None with { Frameworks = [new FrameworkReference("Microsoft.WindowsDesktop.App", "10.0.0", "")] };
        var refused = AssemblyWriter.Publish(result.Files, Path.Combine(_directory, "desktop"), "desktop", desktop);
        Assert.Contains("can't run in a Linux container", Assert.Single(refused.Errors));
        Assert.False(Directory.Exists(Path.Combine(_directory, "desktop")));
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
