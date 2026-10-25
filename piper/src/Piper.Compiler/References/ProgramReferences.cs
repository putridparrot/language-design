using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Piper.Diagnostics;
using Piper.Text;

namespace Piper;

/// <summary>A shared framework a program uses (<c>Microsoft.AspNetCore.App</c>), as installed with the runtime.</summary>
public sealed record FrameworkReference(string Name, string Version, string Directory);

/// <summary>
/// What a program references beyond the platform, resolved to implementation assemblies: those of its NuGet packages
/// (with their dependencies), of its shared frameworks, and its own. The binder reflects over these, Roslyn compiles
/// against them, <see cref="Runner"/> runs with them, and <see cref="AssemblyWriter"/> copies those that aren't part of
/// a framework next to a built program.
/// </summary>
/// <param name="Assemblies">Every referenced assembly's path.</param>
/// <param name="Frameworks">The shared frameworks, whose assemblies are in <paramref name="Assemblies"/> but aren't copied.</param>
public sealed record ProgramReferences(ImmutableArray<string> Assemblies, ImmutableArray<FrameworkReference> Frameworks)
{
    public static ProgramReferences None { get; } = new([], []);

    public bool IsEmpty => Assemblies.IsEmpty && Frameworks.IsEmpty;

    /// <summary>
    /// What Roslyn compiles the program against: <see cref="Assemblies"/>, but with a framework's reference assemblies
    /// (its targeting pack, from the SDK) in place of its implementation ones where they're installed, as the SDK does.
    /// A framework's implementation assemblies can declare the same public type twice, which C# rejects as ambiguous.
    /// </summary>
    public ImmutableArray<string> CompileAssemblies
    {
        get => _compileAssemblies.IsDefault ? Assemblies : _compileAssemblies;
        init => _compileAssemblies = value;
    }

    private readonly ImmutableArray<string> _compileAssemblies;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImmutableDictionary<string, string>> AliasCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extern aliases (assembly name → alias) for the <see cref="CompileAssemblies"/> that declare a public type some
    /// other one also declares: ASP.NET Core has two <c>Microsoft.Extensions.Logging.LoggingBuilderExtensions</c>. C#
    /// tells such types apart only by alias, so the generated code names every type from these assemblies with theirs
    /// (<c>piper_ref1::Microsoft.Extensions.Logging.LoggingBuilderExtensions</c>), and Roslyn references them by it.
    /// </summary>
    public ImmutableDictionary<string, string> Aliases =>
        CompileAssemblies.IsEmpty
            ? ImmutableDictionary<string, string>.Empty
            : AliasCache.GetOrAdd(string.Join(Path.PathSeparator, CompileAssemblies), _ => FindAliases(CompileAssemblies));

    private static ImmutableDictionary<string, string> FindAliases(ImmutableArray<string> assemblies)
    {
        var declaredIn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in assemblies)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
                foreach (var handle in metadata.TypeDefinitions)
                {
                    var type = metadata.GetTypeDefinition(handle);
                    if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                        continue;
                    var fullName = $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
                    if (!declaredIn.TryGetValue(fullName, out var owners))
                        declaredIn[fullName] = owners = [];
                    if (!owners.Contains(name))
                        owners.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
            {
            }
        }
        var clashing = declaredIn.Values.Where(owners => owners.Count > 1).SelectMany(owners => owners).Distinct().Order(StringComparer.Ordinal);
        return clashing.Select((name, i) => (name, i)).ToImmutableDictionary(c => c.name, c => $"piper_ref{c.i + 1}", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The assemblies a built program needs next to it: all but the frameworks', which the .NET host provides.</summary>
    public IEnumerable<string> CopiedAssemblies =>
        Assemblies.Where(path => !Frameworks.Any(f => path.StartsWith(f.Directory, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The runtime's own framework, which every program has.</summary>
    public const string CoreFramework = "Microsoft.NETCore.App";

    /// <summary>
    /// Resolves the <paramref name="packages"/> (id → version), <paramref name="frameworks"/> and assembly
    /// <paramref name="references"/> (relative to <paramref name="directory"/>, the program's folder) a program uses.
    /// Packages are restored by the .NET SDK, through a project generated in the folder's <c>obj/piper</c>; what it
    /// finds is remembered there, so the SDK only runs again when the packages change. Errors are reported against
    /// <paramref name="project"/>, the piper.json, when given.
    /// </summary>
    public static (ProgramReferences References, ImmutableArray<Diagnostic> Errors) Resolve(
        string directory,
        IReadOnlyDictionary<string, string> packages,
        IReadOnlyList<string> frameworks,
        IReadOnlyList<string> references,
        SourceText? project = null)
    {
        var errors = ImmutableArray.CreateBuilder<Diagnostic>();
        void Error(string code, string message) =>
            errors.Add(new Diagnostic(code, DiagnosticSeverity.Error, default, message) { Source = project });

        var assemblies = new List<string>();
        var compile = new List<string>();
        var resolvedFrameworks = new List<FrameworkReference>();
        foreach (var name in frameworks.Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !f.Equals(CoreFramework, StringComparison.OrdinalIgnoreCase)))
        {
            if (InstalledFrameworks.GetOrAdd(name, FindFramework) is not { } framework)
            {
                Error("PIP0402", $"The shared framework '{name}' isn't installed for .NET {Environment.Version.Major}.{Environment.Version.Minor}; see 'dotnet --list-runtimes'.");
                continue;
            }
            resolvedFrameworks.Add(framework.Framework);
            assemblies.AddRange(framework.Assemblies);
            compile.AddRange(framework.Compile);
        }

        var others = new List<string>();
        if (packages.Count > 0)
        {
            var (restored, error) = RestorePackages(directory, packages, resolvedFrameworks);
            if (error is not null)
                Error("PIP0401", error);
            others.AddRange(restored);
        }

        foreach (var reference in references)
        {
            var path = Path.GetFullPath(Path.Combine(directory, reference));
            if (!File.Exists(path))
                Error("PIP0403", $"The referenced assembly '{reference}' doesn't exist.");
            else if (!IsManagedAssembly(path))
                Error("PIP0403", $"'{reference}' isn't a .NET assembly.");
            else
                others.Add(path);
        }

        var resolved = new ProgramReferences([.. assemblies.Concat(others).Distinct(StringComparer.OrdinalIgnoreCase)], [.. resolvedFrameworks])
        {
            CompileAssemblies = [.. compile.Concat(others).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
        return (resolved, errors.ToImmutable());
    }

    /// <summary>The frameworks found so far, with their assemblies; installed frameworks don't change while Piper runs.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InstalledFramework?> InstalledFrameworks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Assemblies">The framework's implementation assemblies, which programs run with.</param>
    /// <param name="Compile">Its reference assemblies, which programs compile against (or the implementation ones, without the SDK).</param>
    private sealed record InstalledFramework(FrameworkReference Framework, ImmutableArray<string> Assemblies, ImmutableArray<string> Compile);

    /// <summary>
    /// The installed version of shared framework <paramref name="name"/> to run with (the one matching this runtime's
    /// version if there is one, else this major and minor version's latest patch), with its assemblies.
    /// </summary>
    private static InstalledFramework? FindFramework(string name)
    {
        // The runtime is at <dotnet>/shared/Microsoft.NETCore.App/<version>/, beside the other frameworks; the SDK's
        // targeting packs, with the reference assemblies, are at <dotnet>/packs/<name>.Ref/<version>/ref/<tfm>/.
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (shared is null || Path.GetDirectoryName(shared) is not { } root || BestVersion(Path.Combine(shared, name)) is not { } runtime)
            return null;

        var assemblies = ManagedAssemblies(runtime);
        var tfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        var pack = BestVersion(Path.Combine(root, "packs", name + ".Ref"), preferred: Path.GetFileName(runtime));
        var reference = pack is null ? null : Path.Combine(pack, "ref", tfm);
        var compile = reference is not null && Directory.Exists(reference) ? ManagedAssemblies(reference) : assemblies;
        return new InstalledFramework(new FrameworkReference(name, Path.GetFileName(runtime), runtime), assemblies, compile);
    }

    /// <summary>
    /// The folder in <paramref name="folder"/> named for the version to use: <paramref name="preferred"/>, else the one
    /// matching this runtime's version, else this major and minor version's latest patch. Null if there's none.
    /// </summary>
    private static string? BestVersion(string folder, string? preferred = null)
    {
        if (!Directory.Exists(folder))
            return null;
        var runtime = Environment.Version;
        return Directory.EnumerateDirectories(folder)
            .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path).Split('-')[0], out var version) ? version : null))
            .Where(c => c.Version is { } v && v.Major == runtime.Major && v.Minor == runtime.Minor)
            .OrderByDescending(c => Path.GetFileName(c.Path) == preferred)
            .ThenByDescending(c => c.Version!.Build == runtime.Build)
            .ThenByDescending(c => c.Version)
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    private static ImmutableArray<string> ManagedAssemblies(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*.dll").Where(IsManagedAssembly).Order(StringComparer.OrdinalIgnoreCase)];

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Restores <paramref name="packages"/> with the .NET SDK and returns their runtime assemblies (with their
    /// dependencies'), or an error. The result is cached in <c>obj/piper/references.json</c> under a hash of the inputs.
    /// </summary>
    private static (IReadOnlyList<string> Assemblies, string? Error) RestorePackages(
        string directory, IReadOnlyDictionary<string, string> packages, IReadOnlyList<FrameworkReference> frameworks)
    {
        var tfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", tfm),
                // Packages a framework already provides are pruned; saying so isn't useful here.
                new XElement("NoWarn", "NU1510")),
            new XElement("ItemGroup",
                packages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new XElement("PackageReference", new XAttribute("Include", p.Key), new XAttribute("Version", p.Value))),
                frameworks.Select(f => new XElement("FrameworkReference", new XAttribute("Include", f.Name)))));
        var text = project.ToString();

        var work = Path.Combine(Path.GetFullPath(directory), "obj", "piper");
        var cachePath = Path.Combine(work, "references.json");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (ReadCache(cachePath, key) is { } cached)
            return (cached, null);

        try
        {
            var restoreDirectory = Path.Combine(work, "restore");
            Directory.CreateDirectory(restoreDirectory);
            var projectPath = Path.Combine(restoreDirectory, "packages.csproj");
            File.WriteAllText(projectPath, text);

            var start = new ProcessStartInfo(DotNetHost())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = restoreDirectory,
            };
            foreach (var argument in new[] { "msbuild", projectPath, "-restore", "-nologo", "-t:ResolvePackageAssets", "-getItem:RuntimeCopyLocalItems" })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill(entireProcessTree: true);
                return ([], "Restoring packages took more than 5 minutes.");
            }

            if (process.ExitCode != 0)
            {
                var lines = (output.Result + error.Result).Split('\n')
                    .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line.Trim())
                    .Distinct();
                return ([], $"Can't restore the packages: {string.Join(" ", lines)}");
            }

            // The output is MSBuild's JSON, maybe after warnings.
            var json = output.Result[output.Result.IndexOf('{')..];
            using var document = JsonDocument.Parse(json);
            var assemblies = document.RootElement.GetProperty("Items").GetProperty("RuntimeCopyLocalItems").EnumerateArray()
                .Select(item => item.GetProperty("FullPath").GetString()!)
                .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new Cache(key, assemblies)));
            return (assemblies, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or ArgumentOutOfRangeException)
        {
            return ([], $"Can't restore the packages: {ex.Message} (Restoring packages needs the .NET SDK.)");
        }
    }

    private sealed record Cache(string Key, List<string> Assemblies);

    private static List<string>? ReadCache(string path, string key)
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(path)) is { } cache &&
                   cache.Key == key && cache.Assemblies.All(File.Exists)
                ? cache.Assemblies
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>dotnet</c> command: the one running this process's tools, or the one beside this runtime.</summary>
    private static string DotNetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
            return host;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
            RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
        var beside = root is null ? null : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return beside is not null && File.Exists(beside) ? beside : "dotnet";
    }
}
