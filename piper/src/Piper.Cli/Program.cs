using Piper;
using Piper.Text;

const string Usage = """
    Usage:
      piper build <inputs...> [-o <dir>]   Transpile to C#: one .cs per .piper (default: next to each source file)
      piper emit  <inputs...>              Print the generated C# to stdout
      piper run   <inputs...>              Transpile, compile and run

    Inputs are .piper files or directories (searched recursively for .piper files);
    all of them are compiled together as one program.
    """;

if (args.Length < 2 || args[0] is not ("build" or "emit" or "run"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var inputs = new List<string>();
string? outputDirectory = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] is "-o" or "--output" && i + 1 < args.Length)
        outputDirectory = args[++i];
    else if (args[i].StartsWith('-'))
    {
        Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
        Console.Error.WriteLine(Usage);
        return 2;
    }
    else
        inputs.Add(args[i]);
}

var files = new List<string>();
foreach (var input in inputs)
{
    if (Directory.Exists(input))
        files.AddRange(Directory.GetFiles(input, "*.piper", SearchOption.AllDirectories).Order());
    else if (File.Exists(input))
        files.Add(input);
    else
    {
        Console.Error.WriteLine($"File not found: {input}");
        return 2;
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("No .piper files to compile.");
    return 2;
}

var sources = files.Select(path => new SourceText(File.ReadAllText(path), path)).ToList();
var result = Compilation.Compile(sources);
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
        for (int i = 0; i < result.Files.Length; i++)
        {
            var directory = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(files[i]))!;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, result.Files[i].Name);
            File.WriteAllText(path, result.Files[i].Text);
            Console.WriteLine($"Wrote {path}");
        }
        return 0;

    default:
        var run = Runner.Run(result.Files, captureOutput: false);
        foreach (var error in run.Errors)
            Console.Error.WriteLine(error);
        return run.Success ? 0 : 3;
}
