using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Piper.Emit;

namespace Piper;

/// <param name="Pdb">Portable debug information, so stack traces can name source lines (Piper ones, given <c>#line</c>).</param>
public sealed record CSharpBuildResult(byte[]? Assembly, byte[]? Pdb, ImmutableArray<string> Errors)
{
    public bool Success => Assembly is not null;
}

public sealed record RunResult(bool Success, string Output, ImmutableArray<string> Errors);

/// <summary>Compiles generated C# in memory with Roslyn and runs it.</summary>
public static class Runner
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray());

    // Console.Out is process-wide, so capturing it must be serialized.
    private static readonly Lock ConsoleLock = new();

    public static CSharpBuildResult Build(string csharp, string assemblyName = "PiperProgram") =>
        Build([new GeneratedFile("Program.cs", csharp)], assemblyName);

    /// <param name="executable">Build a console application whose entry point is the program's `main`, rather than a library.</param>
    public static CSharpBuildResult Build(IEnumerable<GeneratedFile> files, string assemblyName = "PiperProgram", bool executable = false)
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Text, options, path: f.Name, encoding: System.Text.Encoding.UTF8)).ToList();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            References.Value,
            new CSharpCompilationOptions(
                executable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                mainTypeName: executable ? CSharpEmitter.EntryPointClassName : null,
                nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(stream, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        var errors = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(FormatInternalError)
            .ToImmutableArray();

        return result.Success ? new CSharpBuildResult(stream.ToArray(), pdb.ToArray(), errors) : new CSharpBuildResult(null, null, errors);
    }

    /// <summary>
    /// A program that type-checks must compile, so a C# error in the generated code is a bug in Piper. With
    /// <c>#line</c> directives its location is the Piper line that produced the code.
    /// </summary>
    private static string FormatInternalError(Diagnostic diagnostic)
    {
        var location = diagnostic.Location.GetMappedLineSpan();
        var where = location.IsValid ? $"{location.Path}({location.StartLinePosition.Line + 1},{location.StartLinePosition.Character + 1}): " : "";
        return $"{where}internal compiler error: the generated C# doesn't compile ({diagnostic.Id}: {diagnostic.GetMessage()}). " +
               "This is a bug in Piper; please report it.";
    }

    /// <param name="captureOutput">When true, the program's stdout is returned in <see cref="RunResult.Output"/> instead of printed.</param>
    public static RunResult Run(string csharp, bool captureOutput = true) =>
        Run([new GeneratedFile("Program.cs", csharp)], captureOutput);

    public static RunResult Run(IEnumerable<GeneratedFile> files, bool captureOutput = true)
    {
        var build = Build(files);
        if (!build.Success)
            return new RunResult(false, "", build.Errors);

        var context = new AssemblyLoadContext("Piper", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(build.Assembly!), new MemoryStream(build.Pdb!));
            var entryPoint = assembly.GetType(CSharpEmitter.EntryPointClassName)?.GetMethod("Main");
            if (entryPoint is null)
                return new RunResult(false, "", ["The program has no 'Main' function."]);

            return captureOutput ? RunCaptured(entryPoint) : RunDirect(entryPoint);
        }
        finally
        {
            context.Unload();
        }
    }

    private static RunResult RunCaptured(MethodInfo entryPoint)
    {
        lock (ConsoleLock)
        {
            var original = Console.Out;
            var output = new StringWriter { NewLine = "\n" };
            Console.SetOut(output);
            try
            {
                return RunDirect(entryPoint) with { Output = output.ToString() };
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }

    /// <summary>
    /// The frames of <paramref name="exception"/>'s stack trace that are Piper code (those whose <c>#line</c>-mapped
    /// file is a <c>.piper</c> file), as <c>   at main in hello.piper:line 12</c>; empty without line directives.
    /// </summary>
    private static string PiperStackTrace(Exception exception)
    {
        var frames = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: true).GetFrames()
            .Where(f => f.GetFileName() is { } file && file.EndsWith(".piper", StringComparison.OrdinalIgnoreCase))
            .Select(f => $"\n   at {f.GetMethod()?.Name} in {f.GetFileName()}:line {f.GetFileLineNumber()}");
        return string.Concat(frames);
    }

    private static RunResult RunDirect(MethodInfo entryPoint)
    {
        // The generated Main switches to the invariant culture; don't leak that into the host thread.
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            entryPoint.Invoke(null, null);
            return new RunResult(true, "", []);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            return new RunResult(false, "", [$"Unhandled exception: {inner.GetType().FullName}: {inner.Message}{PiperStackTrace(inner)}"]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
