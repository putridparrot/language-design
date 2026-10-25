using System.Runtime.CompilerServices;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// Every program in samples/ compiles without warnings, runs, and passes its tests, so the samples can't go stale.
/// A sample is a .piper file or a folder of them (one program).
/// </summary>
public class SamplesTests
{
    private static string SamplesDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "samples"));

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(SamplesDirectory(), "*.piper").Order())
            data.Add(Path.GetFileName(file));
        foreach (var folder in Directory.GetDirectories(SamplesDirectory()).Order())
            data.Add(Path.GetFileName(folder));
        return data;
    }

    private static List<SourceText> Load(string name)
    {
        var path = Path.Combine(SamplesDirectory(), name);
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.piper", SearchOption.AllDirectories).Order().ToArray() : [path];
        return [.. files.Select(f => new SourceText(File.ReadAllText(f), f))];
    }

    /// <summary>Compiling options for a sample, with the plugins and references its piper.json lists, as the CLI loads them.</summary>
    private static CompilationOptions Options(string name, bool includeTests)
    {
        var options = new CompilationOptions(LineDirectives: true, IncludeTests: includeTests);
        var folder = Path.Combine(SamplesDirectory(), name);
        if (!File.Exists(Path.Combine(folder, PiperProject.FileName)))
            return options;
        var (project, errors) = LoadedProject.Load(folder);
        Assert.Empty(errors);
        return options with { Plugins = project!.Plugins, References = project.References };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Runs(string name)
    {
        var result = Compilation.Compile(Load(name), Options(name, includeTests: false));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);

        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.NotEmpty(run.Output);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Passes_its_tests(string name)
    {
        var result = Compilation.Compile(Load(name), Options(name, includeTests: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var tests = Runner.RunTests(result.Files, references: result.References);
        Assert.True(tests.Success, string.Join("\n", tests.Errors.Concat(tests.Results.Where(r => !r.Passed).Select(r => $"{r.Name}: {r.Failure}"))));
    }
}
