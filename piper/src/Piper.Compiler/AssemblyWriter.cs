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

    /// <param name="references">
    /// What the program references beyond the platform: package and other assemblies are copied next to it, and an
    /// executable's runtimeconfig.json names the shared frameworks.
    /// </param>
    /// <param name="launcher">For an executable, whether to make a native launcher for this machine (not for a container).</param>
    public static AssemblyOutput Write(
        IEnumerable<GeneratedFile> files, string directory, string name, bool executable, ProgramReferences? references = null, bool launcher = true)
    {
        references ??= ProgramReferences.None;
        var build = Runner.Build(files, name, executable, references);
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

        // Package and referenced assemblies go next to the program, where .NET looks for them.
        foreach (var assembly in references.CopiedAssemblies)
            Write(Path.GetFileName(assembly), File.ReadAllBytes(assembly));
        // And their native libraries, for this platform: .NET's DllImport looks beside the program too.
        foreach (var native in references.NativeLibraries)
            Write(Path.GetFileName(native), File.ReadAllBytes(native));

        if (!executable)
            return new AssemblyOutput([.. written], null, []);

        Write(name + ".runtimeconfig.json", Encoding.UTF8.GetBytes(RuntimeConfig(references.Frameworks)));
        var made = launcher ? WriteLauncher(directory, name) : null;
        if (made is not null)
            written.Add(made);
        return new AssemblyOutput([.. written], made, []);
    }

    /// <summary>
    /// <c>piper build --publish</c>: the executable, ready to run on .NET's container image, and a Dockerfile building
    /// that image from the folder, so <c>docker build</c> needs neither the SDK nor the program's source. It's
    /// framework-dependent, on <c>mcr.microsoft.com/dotnet/aspnet</c> when the program uses ASP.NET Core and
    /// <c>…/runtime</c> otherwise, of this .NET's version. There's no native launcher (the image runs
    /// <c>dotnet name.dll</c>); <paramref name="references"/>' native libraries should be the container's platform's
    /// (resolved for <c>linux-x64</c>, say).
    /// </summary>
    public static AssemblyOutput Publish(IEnumerable<GeneratedFile> files, string directory, string name, ProgramReferences references)
    {
        if (references.Frameworks.Any(f => f.Name == "Microsoft.WindowsDesktop.App"))
            return new AssemblyOutput([], null, ["A desktop program (Microsoft.WindowsDesktop.App) can't run in a Linux container; build it with --exe instead."]);
        var output = Write(files, directory, name, executable: true, references, launcher: false);
        if (!output.Success)
            return output;

        var version = $"{Environment.Version.Major}.{Environment.Version.Minor}";
        var image = references.Frameworks.Any(f => f.Name == "Microsoft.AspNetCore.App") ? "aspnet" : "runtime";
        var dockerfile = Path.Combine(directory, "Dockerfile");
        File.WriteAllText(dockerfile, $$"""
            # Made by `piper build --publish`: {{name}}, compiled, on .NET {{version}}'s {{image}} image. From this folder:
            #
            #   docker build -t {{name.ToLowerInvariant()}} .
            #   docker run --rm {{name.ToLowerInvariant()}}
            #
            # Arguments after the image name go to the program's Main. A web app listens where its server's `urls` says,
            # which must be on every interface ("http://*:8080") to be reached from outside the container: publish the
            # port with -p 8080:8080.
            FROM mcr.microsoft.com/dotnet/{{image}}:{{version}}
            WORKDIR /app
            COPY . .
            ENTRYPOINT ["dotnet", "{{name}}.dll"]

            """.ReplaceLineEndings("\n"));
        return output with { Files = output.Files.Add(dockerfile) };
    }

    /// <summary>
    /// Tells the .NET host which runtime to run the program on: this one's major and minor version, or later patches,
    /// with any other shared <paramref name="frameworks"/> the program uses.
    /// </summary>
    private static string RuntimeConfig(ImmutableArray<FrameworkReference> frameworks)
    {
        var version = Environment.Version;
        var names = frameworks.Select(f => f.Name).Prepend(ProgramReferences.CoreFramework);
        var entries = string.Join(",\n", names.Select(framework => $$"""
                  {
                    "name": "{{framework}}",
                    "version": "{{version.Major}}.{{version.Minor}}.0"
                  }
            """));
        return $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{version.Major}}.{{version.Minor}}",
                "frameworks": [
            {{entries}}
                ]
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
