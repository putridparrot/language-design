using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// Piper programs compiled against a .NET library built from C# for the test, for .NET features no platform type shows
/// (required members, interfaces of particular shapes). Each instance has a temporary folder of its own.
/// </summary>
public sealed class LibraryPrograms : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "piper-library-" + Guid.NewGuid().ToString("N"));

    public LibraryPrograms() => Directory.CreateDirectory(_directory);

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

    /// <summary>Compiles <paramref name="program"/> as main.piper, referencing <paramref name="library"/> built as <paramref name="name"/>.dll.</summary>
    public CompilationResult Compile(string name, string library, string program)
    {
        var folder = Path.Combine(_directory, "app");
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        var build = Runner.Build([new GeneratedFile(name + ".cs", library)], name);
        Assert.True(build.Success, string.Join("\n", build.Errors));
        File.WriteAllBytes(Path.Combine(folder, "lib", name + ".dll"), build.Assembly!);
        File.WriteAllText(Path.Combine(folder, PiperProject.FileName), $$"""{ "references": ["lib/{{name}}.dll"] }""");

        var (project, errors) = LoadedProject.Load(folder);
        Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.Format())));
        return Compilation.Compile([new SourceText(program, "main.piper")], new CompilationOptions { References = project!.References });
    }

    /// <summary>Runs a program that compiled, returning what it printed, with Unix line endings.</summary>
    public static string Run(CompilationResult result)
    {
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }
}
