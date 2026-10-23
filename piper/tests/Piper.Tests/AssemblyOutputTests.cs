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
            fn main() {
                let (q, r) = (17 / 5, 17 % 5);
                print("{q} r {r}");
            }
            """);

        var output = AssemblyWriter.Write(result.Files, _directory, "app", executable: true);

        Assert.True(output.Success, string.Join("\n", output.Errors));
        foreach (var file in new[] { "app.dll", "app.pdb", "app.runtimeconfig.json", "Piper.Runtime.dll" })
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

            fn main() {
                print(fail());
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
}
