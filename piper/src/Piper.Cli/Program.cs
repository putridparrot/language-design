using Piper;
using Piper.Text;

const string Usage = """
    Usage:
      piper build <file.piper> [-o <output.cs>]   Transpile to C# (default: next to the source file)
      piper emit  <file.piper>                    Print the generated C# to stdout
      piper run   <file.piper>                    Transpile, compile and run
    """;

if (args.Length < 2 || args[0] is not ("build" or "emit" or "run"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var inputPath = args[1];
string? outputPath = null;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] is "-o" or "--output" && i + 1 < args.Length)
    {
        outputPath = args[++i];
    }
    else
    {
        Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"File not found: {inputPath}");
    return 2;
}

var source = new SourceText(File.ReadAllText(inputPath), inputPath);
var result = Compilation.Compile(source);
foreach (var diagnostic in result.FormattedDiagnostics)
    Console.Error.WriteLine(diagnostic);

if (!result.Success)
    return 1;

switch (command)
{
    case "emit":
        Console.Write(result.CSharp);
        return 0;

    case "build":
        outputPath ??= Path.ChangeExtension(inputPath, ".cs");
        File.WriteAllText(outputPath, result.CSharp);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;

    default:
        var run = Runner.Run(result.CSharp!, captureOutput: false);
        foreach (var error in run.Errors)
            Console.Error.WriteLine(error);
        return run.Success ? 0 : 3;
}
