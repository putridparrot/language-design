using System.Collections.Immutable;

namespace Piper.Plugins;

/// <summary>
/// Plugins added with <c>piper plugin add</c>, which a piper.json names as it does those that come with piper
/// (<c>"plugins": ["state-machines"]</c>). They're a folder per plugin in the user's plugins folder
/// (<c>~/.piper/plugins/state-machines/</c>), searched after piper's own, so adding one needs no new copy of piper, and
/// the language server finds it at its next analysis.
/// </summary>
public static class AddedPlugins
{
    /// <summary>The user's plugins folder: <c>PIPER_PLUGINS</c> if it's set, or <c>.piper/plugins</c> in their home folder.</summary>
    public static string Directory { get; set; } =
        Environment.GetEnvironmentVariable("PIPER_PLUGINS") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".piper", "plugins");

    /// <summary>The names of the added plugins, such as <c>state-machines</c>.</summary>
    public static ImmutableArray<string> Names =>
        System.IO.Directory.Exists(Directory)
            ? [.. System.IO.Directory.GetDirectories(Directory).Where(folder => AssemblyIn(folder) is not null).Select(Path.GetFileName).OfType<string>().Order()]
            : [];

    /// <summary>The assembly of the added plugin <paramref name="name"/>, or null if there's none.</summary>
    internal static string? Find(string name) => AssemblyIn(Path.Combine(Directory, name));

    /// <summary>A folder's plugin assembly: its one <c>Piper.Plugins.*.dll</c>, or, with none, its only .dll.</summary>
    internal static string? AssemblyIn(string folder)
    {
        if (!System.IO.Directory.Exists(folder))
            return null;
        return System.IO.Directory.GetFiles(folder, "Piper.Plugins.*.dll") switch
        {
            [var named] => named,
            [] when System.IO.Directory.GetFiles(folder, "*.dll") is [var only] => only,
            _ => null,
        };
    }

    /// <summary>
    /// Adds the plugin at <paramref name="source"/>: a plugin project's folder (whose <c>bin/plugin</c> is its build
    /// output, as this repository's plugins have), a folder holding the plugin's assembly, or the assembly itself. Its
    /// folder is copied, with the assemblies beside the plugin's, and replaces any added under the same name. The name
    /// is the plugin's own (<see cref="IPiperPlugin.Name"/>) unless <paramref name="name"/> gives another.
    /// </summary>
    /// <returns>The name a piper.json uses for it, or why it couldn't be added.</returns>
    public static (string? Name, string? Error) Add(string source, string? name = null)
    {
        source = Path.GetFullPath(source);
        string folder, assembly;
        if (File.Exists(source))
        {
            if (!source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return (null, $"{source} isn't a plugin: give a plugin's folder, or its assembly (a .dll).");
            folder = Path.GetDirectoryName(source)!;
            assembly = source;
            // Once copied, the plugin is found by its folder's rule, so the assembly must be the one that rule finds.
            if (!string.Equals(AssemblyIn(folder), assembly, StringComparison.OrdinalIgnoreCase))
                return (null, $"{Path.GetFileName(assembly)} has other assemblies beside it, so to be found among them its name needs to start 'Piper.Plugins.'.");
        }
        else if (System.IO.Directory.Exists(source))
        {
            var output = Path.Combine(source, "bin", "plugin");
            folder = System.IO.Directory.Exists(output) ? output : source;
            if (AssemblyIn(folder) is not { } found)
                return (null, $"{folder} has no plugin assembly: it needs one Piper.Plugins.*.dll, or only one .dll. Build the plugin first, or give the path to its assembly.");
            assembly = found;
        }
        else
        {
            return (null, $"{source} doesn't exist.");
        }

        var (plugins, errors) = PluginLoader.Load(folder, [assembly]);
        if (errors.Length > 0)
            return (null, errors[0].Message);
        if (name is null)
        {
            var names = plugins.Select(plugin => plugin.Name).Distinct().ToList();
            if (names.Count != 1)
                return (null, $"{Path.GetFileName(assembly)} has several plugins ({string.Join(", ", names)}); choose a name for them with --name.");
            name = names[0];
        }
        if (!PluginLoader.IsBundledName(name))
            return (null, $"'{name}' can't name a plugin in a piper.json: it can't have '/', '\\' or '.' in it. Choose another with --name.");
        if (PluginLoader.BundledNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (null, $"'{name}' is the name of a plugin that comes with piper, which piper.json files would still get; choose another with --name.");

        var target = Path.Combine(Directory, name);
        try
        {
            if (System.IO.Directory.Exists(target))
                System.IO.Directory.Delete(target, recursive: true);
            Copy(folder, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, $"couldn't copy the plugin to {target}: {exception.Message}");
        }
        return (name, null);
    }

    /// <summary>Removes the added plugin <paramref name="name"/>; false if there's none.</summary>
    public static bool Remove(string name)
    {
        var folder = Path.Combine(Directory, name);
        if (!PluginLoader.IsBundledName(name) || !System.IO.Directory.Exists(folder))
            return false;
        System.IO.Directory.Delete(folder, recursive: true);
        return true;
    }

    private static void Copy(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var file in System.IO.Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var folder in System.IO.Directory.GetDirectories(from))
            Copy(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
