using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Piper.Emit;

namespace Piper;

public sealed record CSharpBuildResult(byte[]? Assembly, ImmutableArray<string> Errors)
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

    public static CSharpBuildResult Build(IEnumerable<GeneratedFile> files, string assemblyName = "PiperProgram")
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Text, options, path: f.Name)).ToList();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        var errors = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToImmutableArray();

        return result.Success ? new CSharpBuildResult(stream.ToArray(), errors) : new CSharpBuildResult(null, errors);
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
            var assembly = context.LoadFromStream(new MemoryStream(build.Assembly!));
            var entryPoint = assembly.GetType(CSharpEmitter.EntryPointClassName)?.GetMethod("Main");
            if (entryPoint is null)
                return new RunResult(false, "", ["The program has no 'main' function."]);

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
            return new RunResult(false, "", [$"Unhandled exception: {inner.GetType().FullName}: {inner.Message}"]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
