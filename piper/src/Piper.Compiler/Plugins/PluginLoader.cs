using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Piper.Diagnostics;
using Piper.Text;

namespace Piper.Plugins;

/// <summary>
/// Loads the plugins a piper.json lists: each assembly's public, non-abstract <see cref="IPiperPlugin"/> types with a
/// parameterless constructor.
/// </summary>
/// <remarks>
/// Each assembly gets its own load context, which shares Piper.Compiler with the compiler (so its types are the
/// compiler's) and resolves the plugin's other dependencies from its folder. The assembly is read into memory rather
/// than loaded from its file, so the language server doesn't lock it, and is reloaded when the file changes.
/// </remarks>
public static class PluginLoader
{
    private static readonly ConcurrentDictionary<string, (DateTime Written, ImmutableArray<IPiperPlugin> Plugins)> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the plugin assemblies at <paramref name="paths"/>, relative to <paramref name="directory"/> (the program's
    /// folder). Errors are reported against <paramref name="project"/>, the piper.json, when given.
    /// </summary>
    public static (ImmutableArray<IPiperPlugin> Plugins, ImmutableArray<Diagnostic> Errors) Load(
        string directory, IEnumerable<string> paths, SourceText? project = null)
    {
        var diagnostics = new DiagnosticBag { CurrentSource = project };
        var plugins = ImmutableArray.CreateBuilder<IPiperPlugin>();
        foreach (var relative in paths)
        {
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            try
            {
                if (!File.Exists(path))
                {
                    diagnostics.ReportPluginLoadFailed(relative, "the file doesn't exist.");
                    continue;
                }
                var written = File.GetLastWriteTimeUtc(path);
                if (!Loaded.TryGetValue(path, out var cached) || cached.Written != written)
                    Loaded[path] = cached = (written, LoadAssembly(path));
                if (cached.Plugins.IsEmpty)
                    diagnostics.ReportPluginLoadFailed(relative, $"it has no public type implementing {nameof(IPiperPlugin)} with a parameterless constructor.");
                plugins.AddRange(cached.Plugins);
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException or ReflectionTypeLoadException or TargetInvocationException or MissingMethodException or TypeLoadException)
            {
                diagnostics.ReportPluginLoadFailed(relative, (exception.InnerException ?? exception).Message);
            }
        }
        return (plugins.ToImmutable(), [.. diagnostics]);
    }

    private static ImmutableArray<IPiperPlugin> LoadAssembly(string path)
    {
        var context = new PluginLoadContext(path);
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        var assembly = context.LoadFromStream(stream);
        return [.. assembly.GetExportedTypes()
            .Where(type => typeof(IPiperPlugin).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false } &&
                           type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (IPiperPlugin)Activator.CreateInstance(type)!)];
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext($"Piper plugin {Path.GetFileName(path)}")
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            // Assemblies the compiler has (Piper.Compiler itself, and the framework's) are shared, not loaded again.
            if (Default.Assemblies.Any(loaded => AssemblyName.ReferenceMatchesDefinition(name, loaded.GetName())))
                return null;
            return _resolver.ResolveAssemblyToPath(name) is { } dependency ? LoadFromAssemblyPath(dependency) : null;
        }
    }
}
