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

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(GoldenDirectory(), "*.piper").Order())
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compiles_and_runs_as_expected(string name)
    {
        var directory = GoldenDirectory();
        var source = new SourceText(File.ReadAllText(Path.Combine(directory, name + ".piper")), name + ".piper");

        var result = Compilation.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));

        var run = Runner.Run(result.CSharp!);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n\n" + result.CSharp);

        Check(Path.Combine(directory, name + ".expected.cs"), result.CSharp!);
        Check(Path.Combine(directory, name + ".expected.out"), run.Output);
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
