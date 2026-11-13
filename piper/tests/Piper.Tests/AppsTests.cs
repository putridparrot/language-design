using System.Runtime.CompilerServices;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The applications in apps/ (real programs written in Piper, such as apps/tracker) compile without warnings and pass
/// their own tests, as `piper test` runs them, so a change to Piper that breaks a real program shows here.
/// </summary>
public class AppsTests
{
    private static string AppsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "apps"));

    public static TheoryData<string> Apps()
    {
        var data = new TheoryData<string>();
        foreach (var folder in Directory.GetDirectories(AppsDirectory()).Order())
            data.Add(Path.GetFileName(folder));
        return data;
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void Passes_its_tests(string name)
    {
        var folder = Path.Combine(AppsDirectory(), name);
        var (project, errors) = LoadedProject.Load(folder);
        Assert.Empty(errors);
        var files = Directory.GetFiles(folder, "*.piper", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Order()
            .Select(f => new SourceText(File.ReadAllText(f), f))
            .ToList();
        var options = new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = project!.Plugins, References = project.References };

        var result = Compilation.Compile(files, options);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var tests = Runner.RunTests(result.Files, references: result.References);
        Assert.True(tests.Success, string.Join("\n", tests.Errors.Concat(tests.Results.Where(r => !r.Passed).Select(r => $"{r.Name}: {r.Failure}"))));
        Assert.NotEmpty(tests.Results);
    }
}
