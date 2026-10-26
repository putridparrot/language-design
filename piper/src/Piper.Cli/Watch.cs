using System.Diagnostics;
using System.Reflection;

/// <summary>
/// <c>piper run app --watch</c>: runs the program, and when a .piper file or piper.json under the inputs changes, stops
/// it and runs it again. Each run is a child <c>piper run</c>, so stopping one (a web server, say) is just ending a
/// process, and a run that fails to compile waits for the next change.
/// </summary>
static class Watch
{
    /// <summary>Set on each run to the watching piper's process id, so the run ends if that does (see <see cref="EndWithWatcher"/>).</summary>
    public const string WatcherVariable = "PIPER_WATCHER_PID";

    /// <summary>
    /// In a run started by <c>--watch</c>: ends this process when the watcher's does, however that ends (Ctrl+C is
    /// handled anyway, but a closed terminal or a killed watcher would otherwise leave a server holding its port).
    /// </summary>
    public static void EndWithWatcher()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(WatcherVariable), out var id))
            return;
        Process watcher;
        try
        {
            watcher = Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            Environment.Exit(130);
            return;
        }
        _ = watcher.WaitForExitAsync().ContinueWith(_ => Environment.Exit(130), TaskScheduler.Default);
    }

    /// <summary>How long changes must settle before a restart, as an editor's save can write several times.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    public static int Run(IReadOnlyList<string> inputs, string[] programArguments)
    {
        var changed = new SemaphoreSlim(0);
        var lastChange = "";
        var watchers = inputs.Select(input => Watcher(input, path =>
        {
            lastChange = path;
            changed.Release();
        })).ToList();

        Process? child = null;
        var stopping = false;
        Console.CancelKeyPress += (_, e) =>
        {
            // The child has the same console, so gets Ctrl+C too and can shut down as it would anyway.
            e.Cancel = true;
            stopping = true;
            changed.Release();
        };

        try
        {
            while (!stopping)
            {
                child = Start(inputs, programArguments);
                var exited = child.WaitForExitAsync();
                var change = changed.WaitAsync();
                if (Task.WaitAny(exited, change) == 0)
                {
                    Console.Error.WriteLine($"[watch] {(child.ExitCode == 0 ? "Finished" : $"Exited with code {child.ExitCode}")}; waiting for a change (Ctrl+C to stop).");
                    change.Wait();
                }
                if (stopping)
                    break;

                // Let a burst of saves finish, then take them as one change.
                while (changed.Wait(Settle)) { }
                Stop(child);
                Console.Error.WriteLine($"[watch] {Path.GetFileName(lastChange)} changed; restarting.");
            }
        }
        finally
        {
            if (child is not null)
                Stop(child, grace: TimeSpan.FromSeconds(5));
            foreach (var watcher in watchers)
                watcher.Dispose();
        }
        return 0;
    }

    /// <summary>Watches a folder (and its subfolders) or a file for changes to the program's sources.</summary>
    private static FileSystemWatcher Watcher(string input, Action<string> onChange)
    {
        var full = Path.GetFullPath(input);
        var isFolder = Directory.Exists(full);
        var watcher = new FileSystemWatcher(isFolder ? full : Path.GetDirectoryName(full)!)
        {
            IncludeSubdirectories = isFolder,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
        };
        void Handle(string path)
        {
            if (isFolder ? IsSource(full, path) : string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                onChange(path);
        }
        watcher.Changed += (_, e) => Handle(e.FullPath);
        watcher.Created += (_, e) => Handle(e.FullPath);
        watcher.Deleted += (_, e) => Handle(e.FullPath);
        watcher.Renamed += (_, e) => { Handle(e.OldFullPath); Handle(e.FullPath); };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>
    /// A .piper file or the piper.json in <paramref name="folder"/>, but not under bin/ or obj/, where builds and package
    /// restores write (and would otherwise restart the program in a loop).
    /// </summary>
    public static bool IsSource(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is [.. var folders, _] &&
            folders.Any(f => f.Equals("bin", StringComparison.OrdinalIgnoreCase) || f.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            return false;
        return path.EndsWith(".piper", StringComparison.OrdinalIgnoreCase) ||
               relative.Equals("piper.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts <c>piper run</c> on the inputs, through the same piper (a launcher, or <c>dotnet piper.dll</c>).</summary>
    private static Process Start(IReadOnlyList<string> inputs, string[] programArguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.Environment[WatcherVariable] = Environment.ProcessId.ToString();
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("run");
        foreach (var input in inputs)
            start.ArgumentList.Add(input);
        start.ArgumentList.Add("--");
        foreach (var argument in programArguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    /// <summary>Ends a run, and anything it started, waiting up to <paramref name="grace"/> for it to end by itself first.</summary>
    private static void Stop(Process child, TimeSpan grace = default)
    {
        if (grace > TimeSpan.Zero && child.WaitForExit(grace))
            return;
        if (!child.HasExited)
            child.Kill(entireProcessTree: true);
        child.WaitForExit();
    }
}
