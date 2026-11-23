using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;

namespace Piper;

/// <summary>
/// Where a program's referenced assemblies (its packages', frameworks' and own) are loaded, for the binder to reflect
/// over and for <see cref="Runner"/> to run. The platform's assemblies are shared with the process, so `System.Object`
/// and the rest are the same types everywhere; anything else named in the references is loaded from there.
/// One context per set of references, kept for the life of the process, as the binder's types come from it.
/// </summary>
public sealed class ReferenceLoadContext : AssemblyLoadContext
{
    private static readonly ConcurrentDictionary<string, ReferenceLoadContext> Contexts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The platform's assemblies (the process's own), by simple name, with their versions.</summary>
    private static readonly Lazy<Dictionary<string, Version?>> PlatformNames = new(() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => VersionOf(g.First()), StringComparer.OrdinalIgnoreCase));

    private static Version? VersionOf(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Version;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            return null;
        }
    }

    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

    private ReferenceLoadContext(ImmutableArray<string> references) : base("Piper references")
    {
        foreach (var path in references)
            _paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
    }

    public static ReferenceLoadContext For(ImmutableArray<string> references) =>
        Contexts.GetOrAdd(string.Join(Path.PathSeparator, references.Order(StringComparer.OrdinalIgnoreCase)), _ => new ReferenceLoadContext(references));

    /// <summary>
    /// Whether <paramref name="name"/> is one of the references, and not a platform assembly, which the process has.
    /// Except where the platform's is only a stand-in older than the version asked for: .NET's core runtime has a
    /// <c>WindowsBase</c> 4.0 for old code, but WPF's PresentationFramework needs the Windows Desktop framework's 10.0,
    /// which the references have.
    /// </summary>
    public bool Has(AssemblyName name) =>
        name.Name is { } simple && _paths.ContainsKey(simple) &&
        (!PlatformNames.Value.TryGetValue(simple, out var platform) || platform is not null && name.Version is { } wanted && wanted > platform);

    protected override Assembly? Load(AssemblyName name) =>
        Has(name) ? LoadFromAssemblyPath(_paths[name.Name!]) : null;

    /// <summary>Native libraries the references' packages bring (SQLite's <c>e_sqlite3</c>), by name without extension or <c>lib</c>.</summary>
    private readonly ConcurrentDictionary<string, string> _natives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes <paramref name="paths"/>, native libraries for this platform, available to the references'
    /// <c>DllImport</c>s, which look them up by name: <c>e_sqlite3</c> for <c>e_sqlite3.dll</c> or <c>libe_sqlite3.so</c>.
    /// </summary>
    public void UseNativeLibraries(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            _natives.TryAdd(name, path);
            if (name.StartsWith("lib", StringComparison.Ordinal))
                _natives.TryAdd(name[3..], path);
        }
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var name = Path.GetFileNameWithoutExtension(unmanagedDllName);
        return _natives.TryGetValue(name, out var path) || (name.StartsWith("lib", StringComparison.Ordinal) && _natives.TryGetValue(name[3..], out path))
            ? LoadUnmanagedDllFromPath(path)
            : IntPtr.Zero;
    }
}
