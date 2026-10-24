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
    /// <summary>The XML documentation file, from the program's `///` comments.</summary>
    public byte[]? Documentation { get; init; }

    public bool Success => Assembly is not null;
}

public sealed record RunResult(bool Success, string Output, ImmutableArray<string> Errors);

/// <param name="Failure">Why the test failed (the assertion's message or the exception, then the Piper lines), or null if it passed.</param>
public sealed record TestResult(string Name, string? Failure)
{
    public bool Passed => Failure is null;
}

/// <param name="Errors">Errors building the program, in which case no tests ran.</param>
public sealed record TestRunResult(ImmutableArray<TestResult> Results, ImmutableArray<string> Errors)
{
    public bool Success => Errors.IsEmpty && Results.All(r => r.Passed);
}

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
        // Parse doc comments, so `///` comments go into the XML documentation file (without warnings for missing ones).
        var options = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse);
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
        using var documentation = new MemoryStream();
        var result = compilation.Emit(stream, pdb, documentation, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        var errors = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(FormatInternalError)
            .ToImmutableArray();

        return result.Success
            ? new CSharpBuildResult(stream.ToArray(), pdb.ToArray(), errors) { Documentation = documentation.ToArray() }
            : new CSharpBuildResult(null, null, errors);
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

    /// <summary>
    /// Runs the program's tests (compiled with <see cref="CompilationOptions.IncludeTests"/>) in declaration order, each
    /// whose name contains <paramref name="filter"/>, if one is given.
    /// </summary>
    public static TestRunResult RunTests(IEnumerable<GeneratedFile> files, string? filter = null)
    {
        var build = Build(files);
        if (!build.Success)
            return new TestRunResult([], build.Errors);

        var context = new AssemblyLoadContext("PiperTests", isCollectible: true);
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(build.Assembly!), new MemoryStream(build.Pdb!));
            var tests = assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).OrderBy(m => m.MetadataToken))
                .Select(m => (Method: m, Name: m.GetCustomAttribute<Piper.Runtime.PiperTestAttribute>()?.Name))
                .Where(t => t.Name is not null && (filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // As the generated Main does, so results don't depend on the machine's culture.
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            return new TestRunResult([.. tests.Select(t => RunTest(t.Name!, t.Method))], []);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
            context.Unload();
        }
    }

    private static TestResult RunTest(string name, MethodInfo method)
    {
        try
        {
            if (method.Invoke(null, null) is Task task)
                task.GetAwaiter().GetResult();
            return new TestResult(name, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            return new TestResult(name, Failure(inner));
        }
        catch (Exception ex)
        {
            return new TestResult(name, Failure(ex));
        }

        static string Failure(Exception exception)
        {
            var message = exception is Piper.Runtime.PiperAssertionException
                ? exception.Message
                : $"Unhandled exception: {exception.GetType().FullName}: {exception.Message}";
            // The test's own frame is named after its position; just give its line.
            var frames = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: true).GetFrames()
                .Where(f => f.GetFileName() is { } file && file.EndsWith(".piper", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.GetMethod()?.Name is { } method && !method.StartsWith("PiperTest_", StringComparison.Ordinal) && method != "MoveNext"
                    ? $"\n   at {method} in {f.GetFileName()}:line {f.GetFileLineNumber()}"
                    : $"\n   at {f.GetFileName()}:line {f.GetFileLineNumber()}");
            return message + string.Concat(frames);
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
