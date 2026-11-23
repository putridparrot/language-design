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

    /// <summary>
    /// Native libraries the packages bring for this platform (SQLite's <c>e_sqlite3.dll</c>): loaded for a run, and
    /// copied next to a built program.
    /// </summary>
    public ImmutableArray<string> NativeLibraries { get; init; } = [];

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
        CompileAssemblies.IsEmpty || !Aliased
            ? ImmutableDictionary<string, string>.Empty
            : AliasCache.GetOrAdd(string.Join(Path.PathSeparator, CompileAssemblies), _ => FindAliases(CompileAssemblies));

    /// <summary>
    /// Whether the generated code gives clashing assemblies <see cref="Aliases"/>: not when it's compiled by a .NET
    /// project (Piper.Build), whose references MSBuild passes without them. There, as in the project's own C#, a type
    /// two references declare is ambiguous only where it's named.
    /// </summary>
    public bool Aliased { get; init; } = true;

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
        // A type declared twice among them, or one that redefines a public type of .NET's own (which the program always
        // references, and which keeps the plain name): Microsoft.Extensions.DependencyModel has its own public
        // System.Collections.Generic.CollectionExtensions, beside the core library's.
        var clashing = declaredIn
            .SelectMany(entry => entry.Value.Count > 1 ? entry.Value
                : PlatformTypes.Value.Types.Contains(entry.Key) ? entry.Value.Where(owner => !PlatformTypes.Value.Assemblies.Contains(owner)) : [])
            .Distinct()
            .Order(StringComparer.Ordinal);
        return clashing.Select((name, i) => (name, i)).ToImmutableDictionary(c => c.name, c => $"piper_ref{c.i + 1}", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The public types of .NET's own assemblies (the runtime's folder), and their names: found once.</summary>
    private static readonly Lazy<(HashSet<string> Types, HashSet<string> Assemblies)> PlatformTypes = new(() =>
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (folder is null)
            return (types, assemblies);
        foreach (var path in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                if (!pe.HasMetadata)
                    continue;
                var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                if (metadata.IsAssembly)
                    assemblies.Add(metadata.GetString(metadata.GetAssemblyDefinition().Name));
                foreach (var handle in metadata.TypeDefinitions)
                {
                    var type = metadata.GetTypeDefinition(handle);
                    if ((type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public)
                        types.Add($"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}");
                }
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
            {
            }
        }
        return (types, assemblies);
    });

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
    /// <paramref name="project"/>, the piper.json, when given. Native libraries are those for <paramref name="runtime"/>
    /// (a runtime identifier, such as <c>linux-x64</c> for a container), or for this machine.
    /// </summary>
    public static (ProgramReferences References, ImmutableArray<Diagnostic> Errors) Resolve(
        string directory,
        IReadOnlyDictionary<string, string> packages,
        IReadOnlyList<string> frameworks,
        IReadOnlyList<string> references,
        SourceText? project = null,
        string? runtime = null)
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
        var natives = new List<string>();
        if (packages.Count > 0)
        {
            var (restored, restoredNatives, error) = RestorePackages(directory, packages, resolvedFrameworks, runtime ?? NativeRuntimeIdentifier());
            if (error is not null)
                Error("PIP0401", error);
            others.AddRange(restored);
            natives.AddRange(restoredNatives);
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
            NativeLibraries = [.. natives.Distinct(StringComparer.OrdinalIgnoreCase)],
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
    private static (IReadOnlyList<string> Assemblies, IReadOnlyList<string> Natives, string? Error) RestorePackages(
        string directory, IReadOnlyDictionary<string, string> packages, IReadOnlyList<FrameworkReference> frameworks, string rid)
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
        // One per platform, so publishing for a container doesn't undo what this machine's builds remembered.
        var cachePath = Path.Combine(work, $"references.{rid}.json");
        // Native libraries are per platform, so the platform is part of what's cached; and "runtime assets" since
        // packages' assemblies for this platform have been resolved too, so caches made before then are made again.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text + "\n" + rid + "\nruntime assets")));
        if (ReadCache(cachePath, key) is { } cached)
            return (cached.Assemblies, cached.Natives ?? [], null);

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
            // Managed assemblies, and native libraries: those for every platform (NativeCopyLocalItems), and those under
            // runtimes/<rid>/native for this one (RuntimeTargetsCopyLocalItems), such as SQLite's.
            foreach (var argument in new[] { "msbuild", projectPath, "-restore", "-nologo", "-t:ResolvePackageAssets",
                         "-getItem:RuntimeCopyLocalItems", "-getItem:NativeCopyLocalItems", "-getItem:RuntimeTargetsCopyLocalItems" })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill(entireProcessTree: true);
                return ([], [], "Restoring packages took more than 5 minutes.");
            }

            if (process.ExitCode != 0)
            {
                var lines = (output.Result + error.Result).Split('\n')
                    .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line.Trim())
                    .Distinct();
                return ([], [], $"Can't restore the packages: {string.Join(" ", lines)}");
            }

            // The output is MSBuild's JSON, maybe after warnings.
            var json = output.Result[output.Result.IndexOf('{')..];
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.GetProperty("Items");
            IEnumerable<JsonElement> Items(string name) =>
                items.TryGetProperty(name, out var list) ? list.EnumerateArray() : [];
            string? Metadata(JsonElement item, string name) =>
                item.TryGetProperty(name, out var value) ? value.GetString() : null;

            // A package's assembly for this platform (runtimes/win/lib/net9.0/Microsoft.Data.SqlClient.dll) replaces the one
            // for every platform in lib/, which may only throw PlatformNotSupportedException: the most specific platform's,
            // as `win-x64` falls back to `win`, and `linux-x64` to `linux`, then `unix`.
            var fallbacks = RuntimeFallbacks(rid);
            var forPlatform = Items("RuntimeTargetsCopyLocalItems")
                .Where(item => Metadata(item, "AssetType") == "runtime" && fallbacks.Contains(Metadata(item, "RuntimeIdentifier") ?? ""))
                .Select(item => (Path: item.GetProperty("FullPath").GetString()!, Rank: fallbacks.IndexOf(Metadata(item, "RuntimeIdentifier")!)))
                .Where(item => item.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.MinBy(item => item.Rank).Path, StringComparer.OrdinalIgnoreCase);
            var assemblies = Items("RuntimeCopyLocalItems")
                .Select(item => item.GetProperty("FullPath").GetString()!)
                .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(path => forPlatform.GetValueOrDefault(Path.GetFileName(path), path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var natives = Items("NativeCopyLocalItems")
                .Concat(Items("RuntimeTargetsCopyLocalItems").Where(item =>
                    Metadata(item, "AssetType") == "native" && string.Equals(Metadata(item, "RuntimeIdentifier"), rid, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.GetProperty("FullPath").GetString()!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new Cache(key, assemblies, natives)));
            return (assemblies, natives, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or ArgumentOutOfRangeException)
        {
            return ([], [], $"Can't restore the packages: {ex.Message} (Restoring packages needs the .NET SDK.)");
        }
    }

    /// <summary>
    /// This platform as NuGet's portable runtime identifiers name it (<c>win-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>),
    /// which packages' native libraries are filed under (<c>runtimes/win-x64/native/e_sqlite3.dll</c>).
    /// </summary>
    /// <summary>
    /// The runtime identifiers a package's platform-specific assets for <paramref name="rid"/> may be filed under, most
    /// specific first: <c>win-x64</c>, <c>win</c>; <c>linux-arm64</c>, <c>linux</c>, <c>unix</c>.
    /// </summary>
    private static List<string> RuntimeFallbacks(string rid)
    {
        var os = rid.Split('-')[0];
        List<string> fallbacks = [rid, os];
        if (os is "linux" or "osx" or "linux-musl")
            fallbacks.Add("unix");
        return fallbacks;
    }

    private static string NativeRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }

    private sealed record Cache(string Key, List<string> Assemblies, List<string>? Natives = null);

    private static Cache? ReadCache(string path, string key)
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(path)) is { } cache &&
                   cache.Key == key && cache.Assemblies.All(File.Exists) && (cache.Natives ?? []).All(File.Exists)
                ? cache
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
