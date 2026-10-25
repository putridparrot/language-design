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

    private static readonly Lazy<HashSet<string>> PlatformNames = new(() =>
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()]);

    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

    private ReferenceLoadContext(ImmutableArray<string> references) : base("Piper references")
    {
        foreach (var path in references)
            _paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
    }

    public static ReferenceLoadContext For(ImmutableArray<string> references) =>
        Contexts.GetOrAdd(string.Join(Path.PathSeparator, references.Order(StringComparer.OrdinalIgnoreCase)), _ => new ReferenceLoadContext(references));

    /// <summary>Whether <paramref name="name"/> is one of the references (and not a platform assembly, which the process has).</summary>
    public bool Has(AssemblyName name) => name.Name is { } simple && !PlatformNames.Value.Contains(simple) && _paths.ContainsKey(simple);

    protected override Assembly? Load(AssemblyName name) =>
        Has(name) ? LoadFromAssemblyPath(_paths[name.Name!]) : null;
}
