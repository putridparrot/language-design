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
    private static readonly Lazy<ImmutableArray<MetadataReference>> PlatformReferences = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray());

    /// <summary>The platform's assemblies, then the program's others (those named like a platform one are the platform's).</summary>
    private static ImmutableArray<MetadataReference> References(ProgramReferences? references)
    {
        if (references is null || references.CompileAssemblies.IsEmpty)
            return PlatformReferences.Value;
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // A reference named as a platform assembly is left out for the platform's, unless the platform's is an older
        // stand-in: the core runtime's WindowsBase 4.0, where WPF needs the Windows Desktop framework's.
        var replacing = references.CompileAssemblies
            .Where(path => platform.TryGetValue(Path.GetFileNameWithoutExtension(path), out var own) && Newer(path, own))
            .ToList();
        var replaced = replacing.Select(path => platform[Path.GetFileNameWithoutExtension(path)]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = references.Aliases;
        var kept = PlatformReferences.Value.Where(r => r is not PortableExecutableReference { FilePath: { } file } || !replaced.Contains(file));
        return [.. kept, .. references.CompileAssemblies
            .Where(path => !platform.ContainsKey(Path.GetFileNameWithoutExtension(path)) || replacing.Contains(path))
            .Select(path => aliases.TryGetValue(Path.GetFileNameWithoutExtension(path), out var alias)
                ? MetadataReference.CreateFromFile(path).WithAliases([alias])
                : (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    /// <summary>Whether the assembly at <paramref name="path"/> is a later version than the one at <paramref name="than"/>.</summary>
    private static bool Newer(string path, string than)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Version > AssemblyName.GetAssemblyName(than).Version;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// A context to run a program in, which finds its referenced assemblies (<see cref="ReferenceLoadContext"/>), as the
    /// binder did. Collectible, so the program can be unloaded.
    /// </summary>
    /// <remarks>
    /// Not collectible when the program references packages or other assemblies: those may find the program's types
    /// by name (serializers do), and an assembly that can't be unloaded can't resolve to one that can. Such programs
    /// then stay loaded until the process ends, as <c>piper run</c>'s does anyway.
    /// </remarks>
    private sealed class ProgramLoadContext(string name, ProgramReferences? references)
        : AssemblyLoadContext(name, isCollectible: references is null || references.Assemblies.IsEmpty)
    {
        private readonly ReferenceLoadContext? _references = References(references);

        /// <summary>The references' context, given the packages' native libraries, which their assemblies load there.</summary>
        private static ReferenceLoadContext? References(ProgramReferences? references)
        {
            if (references is not { Assemblies.IsEmpty: false })
                return null;
            var context = ReferenceLoadContext.For(references.Assemblies);
            context.UseNativeLibraries(references.NativeLibraries);
            return context;
        }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            _references is not null && _references.Has(assemblyName) ? _references.LoadFromAssemblyName(assemblyName) : null;
    }

    // Console.Out is process-wide, so it's replaced once by a writer that sends each write to the capture of the run it
    // comes from (an AsyncLocal, which flows into the tasks and threads a program starts), or else to the console.
    // Programs running at once, as tests do, each get their own output, and nothing another writes meanwhile (a web
    // app's log, a test's message) lands in it.
    private static readonly Lock ConsoleLock = new();
    private static TextWriter? _installedOut;

    private sealed class RoutingWriter(TextWriter console) : TextWriter
    {
        public static readonly AsyncLocal<TextWriter?> Capture = new();

        private TextWriter Target => Capture.Value ?? console;

        public override System.Text.Encoding Encoding => console.Encoding;

        public override void Write(char value) => Target.Write(value);

        public override void Write(string? value) => Target.Write(value);

        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);

        public override void WriteLine() => Target.WriteLine();

        public override void WriteLine(string? value) => Target.WriteLine(value);

        public override void Flush() => Target.Flush();
    }

    /// <summary>Makes Console.Out the routing writer, again if something has replaced it since.</summary>
    private static void InstallRouting()
    {
        lock (ConsoleLock)
        {
            if (_installedOut is not null && ReferenceEquals(Console.Out, _installedOut))
                return;
            Console.SetOut(new RoutingWriter(Console.Out));
            _installedOut = Console.Out;
        }
    }

    public static CSharpBuildResult Build(string csharp, string assemblyName = "PiperProgram") =>
        Build([new GeneratedFile("Program.cs", csharp)], assemblyName);

    /// <param name="executable">Build a console application whose entry point is the program's `main`, rather than a library.</param>
    /// <param name="references">What the program references beyond the platform (<see cref="CompilationResult.References"/>).</param>
    public static CSharpBuildResult Build(
        IEnumerable<GeneratedFile> files, string assemblyName = "PiperProgram", bool executable = false, ProgramReferences? references = null, bool windowed = false)
    {
        // Parse doc comments, so `///` comments go into the XML documentation file (without warnings for missing ones).
        var options = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Text, options, path: f.Name, encoding: System.Text.Encoding.UTF8)).ToList();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            References(references),
            new CSharpCompilationOptions(
                !executable ? OutputKind.DynamicallyLinkedLibrary : windowed ? OutputKind.WindowsApplication : OutputKind.ConsoleApplication,
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

    /// <summary>
    /// The programs being run, by assembly name. Code that finds a type by its assembly-qualified name, as serializers
    /// do (<c>"Message+Add, PiperProgram3"</c>), can't see an assembly loaded from memory into its own context, so
    /// <see cref="ResolveRunning"/> finds it here. Each run has its own name, as tests run several at once.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Assembly> Running = new();

    private static int _runs;

    static Runner() => AppDomain.CurrentDomain.AssemblyResolve += ResolveRunning;

    private static Assembly? ResolveRunning(object? sender, ResolveEventArgs args) =>
        new AssemblyName(args.Name).Name is { } name && Running.TryGetValue(name, out var assembly) ? assembly : null;

    /// <summary>
    /// Builds and loads a program, which <see cref="ResolveRunning"/> finds by name until it's unloaded: called
    /// <paramref name="assemblyName"/> if it's given and not running already, or else a name of its own.
    /// </summary>
    private static (Assembly? Assembly, ProgramLoadContext Context, string Name, ImmutableArray<string> Errors) Load(
        IEnumerable<GeneratedFile> files, string contextName, ProgramReferences? references, string? assemblyName = null)
    {
        var name = assemblyName is not null && !Running.ContainsKey(assemblyName) ? assemblyName : $"PiperProgram{Interlocked.Increment(ref _runs)}";
        var context = new ProgramLoadContext(contextName, references);
        var build = Build(files, name, references: references);
        if (!build.Success)
            return (null, context, name, build.Errors);
        var assembly = context.LoadFromStream(new MemoryStream(build.Assembly!), new MemoryStream(build.Pdb!));
        Running[name] = assembly;
        return (assembly, context, name, []);
    }

    private static void Unload(ProgramLoadContext context, string name)
    {
        Running.TryRemove(name, out _);
        if (context.IsCollectible)
            context.Unload();
    }

    /// <param name="args">The command-line arguments, for a `Main(args: string[])`.</param>
    /// <param name="assemblyName">
    /// The program's assembly name, as <c>piper build</c> would give it. Libraries that send types between processes
    /// name them with it (<c>"Message+Add, app"</c>), so a run and a build of the same program then understand each other.
    /// </param>
    public static RunResult Run(
        IEnumerable<GeneratedFile> files, bool captureOutput = true, ProgramReferences? references = null, string[]? args = null, string? assemblyName = null)
    {
        var (assembly, context, name, errors) = Load(files, "Piper", references, assemblyName);
        try
        {
            if (assembly is null)
                return new RunResult(false, "", errors);
            var entryPoint = assembly.GetType(CSharpEmitter.EntryPointClassName)?.GetMethod("Main");
            if (entryPoint is null)
                return new RunResult(false, "", ["The program has no 'Main' function."]);

            return captureOutput ? RunCaptured(entryPoint, args ?? []) : RunDirect(entryPoint, args ?? []);
        }
        finally
        {
            Unload(context, name);
        }
    }

    /// <summary>
    /// Runs the program's tests (compiled with <see cref="CompilationOptions.IncludeTests"/>) in declaration order, each
    /// whose name contains <paramref name="filter"/>, if one is given.
    /// </summary>
    /// <param name="seed">The seed property tests generate values from; by default a new one each run.</param>
    public static TestRunResult RunTests(IEnumerable<GeneratedFile> files, string? filter = null, ProgramReferences? references = null, int? seed = null)
    {
        var (assembly, context, name, errors) = Load(files, "PiperTests", references);
        var culture = Thread.CurrentThread.CurrentCulture;
        var outerSeed = Piper.Runtime.PiperProperty.Seed;
        Piper.Runtime.PiperProperty.Seed = seed;
        try
        {
            if (assembly is null)
                return new TestRunResult([], errors);
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
            Piper.Runtime.PiperProperty.Seed = outerSeed;
            Thread.CurrentThread.CurrentCulture = culture;
            Unload(context, name);
        }
    }

    private static TestResult RunTest(string name, MethodInfo method)
    {
        try
        {
            var parameters = method.GetParameters();
            var attribute = method.GetCustomAttribute<Piper.Runtime.PiperTestAttribute>();
            var simulate = attribute?.Simulate == true;
            if (parameters.Length == 0 && simulate)
            {
                // `simulate`: a run in a simulation for each of so many seeds, from --seed's on (so it repeats one).
                var runs = attribute!.Runs > 0 ? attribute.Runs : Piper.Runtime.PiperTestAttribute.SimulatedRuns;
                Piper.Runtime.PiperSimulation.Explore(runs, _ => InvokeSimulated(method, null), Piper.Runtime.PiperProperty.Seed);
            }
            else if (parameters.Length == 0)
            {
                Invoke(method, null);
            }
            else
            {
                // A property test: each parameter's generator is made by the function named after its position (for
                // an `in` range or collection), or else from its type.
                var generators = parameters.Select((p, i) =>
                    method.DeclaringType!.GetMethod($"{method.Name}_{i}", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is { } make
                        ? (Piper.Runtime.IPiperGen)Invoke(make, null)!
                        : Piper.Runtime.PiperGen.For(p.ParameterType)).ToList();
                List<string> names = [.. parameters.Select(p => p.Name ?? "")];
                var runs = attribute?.Runs is > 0 and var own ? own : Piper.Runtime.PiperProperty.Runs;
                if (simulate)
                {
                    // Simulated, each case's run goes on drawing from the choices its values were made from, so the
                    // values and the run are shrunk together, and repeat with the property's seed.
                    generators.Add(Piper.Runtime.PiperSimulation.Choices);
                    names.Add("simulation");
                    Piper.Runtime.PiperProperty.Check(names, generators,
                        values => Piper.Runtime.PiperSimulation.Run((Piper.Runtime.PiperChoices)values[^1]!, _ => InvokeSimulated(method, values[..^1])), runs);
                }
                else
                {
                    Piper.Runtime.PiperProperty.Check(names, generators, values => Invoke(method, values), runs);
                }
            }
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

        // Calls a test, or a generator's function, waiting for a Task, and throwing what it threw rather than a
        // TargetInvocationException.
        static object? Invoke(MethodInfo method, object?[]? arguments)
        {
            object? result;
            try
            {
                result = method.Invoke(null, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
            if (result is Task task)
                task.GetAwaiter().GetResult();
            return result;
        }

        // Calls a test inside a simulation: its Task is awaited there, not waited for, as the simulation runs on this thread.
        static Task InvokeSimulated(MethodInfo method, object?[]? arguments)
        {
            try
            {
                return method.Invoke(null, arguments) as Task ?? Task.CompletedTask;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                return Task.FromException(inner);
            }
        }

        static string Failure(Exception exception)
        {
            // A simulated run: its seed, which repeats it, why it failed, and the end of what happened, in simulated time.
            if (exception is Piper.Runtime.SimulationFailedException simulation)
            {
                var timeline = simulation.Timeline.Count > 30 ? ["...", .. simulation.Timeline.TakeLast(30)] : simulation.Timeline;
                var why = simulation.InnerException ?? simulation;
                var reason = why is Piper.Runtime.PiperAssertionException ? why.Message : $"{why.GetType().FullName}: {why.Message}";
                var shrunk = simulation.Shrinks == 0 ? ""
                    : $", shrunk {simulation.Shrinks} {(simulation.Shrinks == 1 ? "time" : "times")} to {simulation.Departures} {(simulation.Departures == 1 ? "choice" : "choices")} from the calmest run";
                return $"Simulation failed (seed {simulation.Seed}; piper test --seed {simulation.Seed} repeats it{shrunk}): {reason}" +
                    (timeline.Count == 0 ? "" : "\n     Timeline (simulated time):\n" + string.Join("\n", timeline.Select(line => "     " + line))) +
                    Frames(why);
            }

            // A property test's message names the values; the stack is the smallest failing case's.
            var message = exception is Piper.Runtime.PiperAssertionException or Piper.Runtime.PiperPropertyException
                ? exception.Message
                : $"Unhandled exception: {exception.GetType().FullName}: {exception.Message}";
            if (exception is Piper.Runtime.PiperPropertyException { InnerException: { } failure })
                exception = failure is Piper.Runtime.SimulationFailedException { InnerException: { } why } ? why : failure;
            return message + Frames(exception);
        }

        // The Piper lines the exception passed through. The test's own frame is named after its position; just its line.
        static string Frames(Exception exception) => string.Concat(
            new System.Diagnostics.StackTrace(exception, fNeedFileInfo: true).GetFrames()
                .Where(f => f.GetFileName() is { } file && file.EndsWith(".piper", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.GetMethod()?.Name is { } method && !method.StartsWith("PiperTest_", StringComparison.Ordinal) && method != "MoveNext"
                    ? $"\n   at {method} in {f.GetFileName()}:line {f.GetFileLineNumber()}"
                    : $"\n   at {f.GetFileName()}:line {f.GetFileLineNumber()}"));
    }

    private static RunResult RunCaptured(MethodInfo entryPoint, string[] args)
    {
        InstallRouting();
        var outer = RoutingWriter.Capture.Value;
        var output = new StringWriter { NewLine = "\n" };
        RoutingWriter.Capture.Value = output;
        try
        {
            var result = RunDirect(entryPoint, args);
            // Writes are serialized by Console's synchronized wrapper, which locks on itself.
            lock (_installedOut!)
                return result with { Output = output.ToString() };
        }
        finally
        {
            RoutingWriter.Capture.Value = outer;
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

    private static RunResult RunDirect(MethodInfo entryPoint, string[] args)
    {
        // A `[STAThread]` Main (a WPF or Windows Forms program) runs on a thread of its own in a single-threaded
        // apartment, as .NET starts such a program, rather than on the caller's (`piper run`'s).
        if (OperatingSystem.IsWindows() && entryPoint.IsDefined(typeof(STAThreadAttribute)) &&
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            RunResult? result = null;
            var thread = new Thread(() => result = RunDirect(entryPoint, args));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return result!;
        }
        // The generated Main switches to the invariant culture; don't leak that into the host thread.
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            entryPoint.Invoke(null, [args]);
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
