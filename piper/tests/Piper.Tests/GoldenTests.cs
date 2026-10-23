using System.Runtime.CompilerServices;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// Each Golden/&lt;name&gt;.piper is compiled; the generated C# must match &lt;name&gt;.expected.cs, and running
/// it must print &lt;name&gt;.expected.out. Set PIPER_UPDATE_GOLDEN=1 to (re)write the expected files,
/// then review the diff.
/// </summary>
public class GoldenTests
{
    private static readonly bool Update = Environment.GetEnvironmentVariable("PIPER_UPDATE_GOLDEN") == "1";

    private static string GoldenDirectory([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");

    /// <summary>A case is a single <c>name.piper</c>, or a directory <c>name/</c> whose .piper files form one program.</summary>
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        var directory = GoldenDirectory();
        foreach (var file in Directory.GetFiles(directory, "*.piper").Order())
            data.Add(Path.GetFileNameWithoutExtension(file));
        foreach (var program in Directory.GetDirectories(directory).Order())
            data.Add(Path.GetFileName(program));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compiles_and_runs_as_expected(string name)
    {
        var directory = GoldenDirectory();
        var result = Compilation.Compile(LoadSources(directory, name));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));

        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n\n" + result.CSharp);

        Check(Path.Combine(directory, name + ".expected.cs"), result.CSharp!);
        Check(Path.Combine(directory, name + ".expected.out"), run.Output);
    }

    /// <summary><c>#line</c> directives (used by <c>piper run</c> and <c>build</c>) must not change what compiles or what it prints.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Runs_the_same_with_line_directives(string name)
    {
        var directory = GoldenDirectory();
        var result = Compilation.Compile(LoadSources(directory, name), new CompilationOptions(LineDirectives: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Contains("#line ", result.CSharp);

        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n\n" + result.CSharp);
        Assert.Equal(Normalize(File.ReadAllText(Path.Combine(directory, name + ".expected.out"))), Normalize(run.Output));
    }

    private static List<SourceText> LoadSources(string directory, string name)
    {
        var programDirectory = Path.Combine(directory, name);
        return Directory.Exists(programDirectory)
            ? Directory.GetFiles(programDirectory, "*.piper", SearchOption.AllDirectories).Order()
                .Select(path => new SourceText(File.ReadAllText(path), Path.GetRelativePath(directory, path).Replace('\\', '/')))
                .ToList()
            : [new SourceText(File.ReadAllText(Path.Combine(directory, name + ".piper")), name + ".piper")];
    }

    private static void Check(string expectedPath, string actual)
    {
        if (Update)
        {
            File.WriteAllText(expectedPath, actual);
            return;
        }

        Assert.True(File.Exists(expectedPath), $"Missing {expectedPath}; run the tests with PIPER_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(Normalize(File.ReadAllText(expectedPath)), Normalize(actual));
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");
}
