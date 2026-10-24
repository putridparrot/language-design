using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Piper;

/// <param name="Files">Everything written, the assembly first.</param>
/// <param name="Launcher">For an executable, the native launcher (<c>app.exe</c>), or null if none could be made (run <c>dotnet app.dll</c>).</param>
public sealed record AssemblyOutput(ImmutableArray<string> Files, string? Launcher, ImmutableArray<string> Errors)
{
    public bool Success => Errors.IsEmpty;
}

/// <summary>
/// Writes a compiled Piper program to disk as a .NET assembly: <c>name.dll</c> and <c>name.pdb</c>, the
/// <c>Piper.Runtime.dll</c> it uses, and for an executable <c>name.runtimeconfig.json</c> and a native launcher, made the
/// way the .NET SDK makes one (from its apphost template).
/// </summary>
public static class AssemblyWriter
{
    /// <summary>The placeholder the SDK's apphost template holds where the app's DLL path goes (the SHA-256 of "foobar").</summary>
    private const string AppHostPlaceholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    /// <summary>The space the apphost template reserves for that path.</summary>
    private const int AppHostPathCapacity = 1024;

    public static AssemblyOutput Write(IEnumerable<GeneratedFile> files, string directory, string name, bool executable)
    {
        var build = Runner.Build(files, name, executable);
        if (!build.Success)
            return new AssemblyOutput([], null, build.Errors);

        Directory.CreateDirectory(directory);
        var written = new List<string>();
        void Write(string fileName, byte[] bytes)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, bytes);
            written.Add(path);
        }

        Write(name + ".dll", build.Assembly!);
        Write(name + ".pdb", build.Pdb!);
        Write(name + ".xml", build.Documentation!);

        // Option, Result and the interop helpers, which every compiled Piper program references.
        var runtime = typeof(Piper.Runtime.PiperInterop).Assembly.Location;
        Write(Path.GetFileName(runtime), File.ReadAllBytes(runtime));

        if (!executable)
            return new AssemblyOutput([.. written], null, []);

        Write(name + ".runtimeconfig.json", Encoding.UTF8.GetBytes(RuntimeConfig()));
        var launcher = WriteLauncher(directory, name);
        if (launcher is not null)
            written.Add(launcher);
        return new AssemblyOutput([.. written], launcher, []);
    }

    /// <summary>Tells the .NET host which runtime to run the program on: this one's major and minor version, or later patches.</summary>
    private static string RuntimeConfig()
    {
        var version = Environment.Version;
        return $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{version.Major}}.{{version.Minor}}",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{version.Major}}.{{version.Minor}}.0"
                }
              }
            }

            """;
    }

    /// <summary>
    /// Copies the SDK's apphost template for this platform, with the DLL's name written into its placeholder, as the SDK
    /// does. Null if there's no template (only a runtime is installed) or the placeholder isn't where it's expected.
    /// </summary>
    private static string? WriteLauncher(string directory, string name)
    {
        if (FindAppHostTemplate() is not { } template)
            return null;

        var bytes = File.ReadAllBytes(template);
        var placeholder = Encoding.UTF8.GetBytes(AppHostPlaceholder);
        var appPath = Encoding.UTF8.GetBytes(name + ".dll");
        int index = bytes.AsSpan().IndexOf(placeholder);
        if (index < 0 || appPath.Length >= AppHostPathCapacity)
            return null;
        Array.Clear(bytes, index, placeholder.Length);
        appPath.CopyTo(bytes, index);

        var launcher = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllBytes(launcher, bytes);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(launcher,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return launcher;
    }

    /// <summary>
    /// <c>dotnet/packs/Microsoft.NETCore.App.Host.&lt;rid&gt;/&lt;version&gt;/runtimes/&lt;rid&gt;/native/apphost</c>, for this runtime's
    /// version if present, otherwise the newest with the same major version.
    /// </summary>
    private static string? FindAppHostTemplate()
    {
        // The runtime lives in dotnet/shared/Microsoft.NETCore.App/<version>/.
        var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var rid = RuntimeInformation.RuntimeIdentifier;
        var pack = Path.Combine(dotnetRoot, "packs", $"Microsoft.NETCore.App.Host.{rid}");
        if (!Directory.Exists(pack))
            return null;

        var current = Environment.Version;
        var versions = Directory.GetDirectories(pack)
            .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var v) ? v : null))
            .Where(v => v.Version is { } version && version.Major == current.Major)
            .OrderByDescending(v => v.Version == new Version(current.Major, current.Minor, current.Build))
            .ThenByDescending(v => v.Version)
            .Select(v => v.Path);
        var fileName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
        return versions
            .Select(version => Path.Combine(version, "runtimes", rid, "native", fileName))
            .FirstOrDefault(File.Exists);
    }
}
