using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Piper;

/// <summary>
/// <c>piper run app --watch</c>: runs the program, and when a .piper file or piper.json under the inputs changes, stops
/// it and runs it again. Each run is a child <c>piper run</c>, so stopping one (a web server, say) is just ending a
/// process, and a run that fails to compile waits for the next change.
/// <para>
/// A web program's pages reload too: the watcher serves an event stream on loopback, and each run of a program using
/// ASP.NET Core loads Piper.Watch (see <see cref="WithBrowserReload"/>), which puts a script in each page that listens
/// to it. After a restart the watcher sends "reload", and the pages reload once the program answers again.
/// </para>
/// </summary>
static class Watch
{
    /// <summary>Set on each run to the watching piper's process id, so the run ends if that does (see <see cref="EndWithWatcher"/>).</summary>
    public const string WatcherVariable = "PIPER_WATCHER_PID";

    /// <summary>Set on each run to the watcher's event stream; Piper.Watch's pages listen to it.</summary>
    private const string ReloadVariable = "PIPER_WATCH_RELOAD";

    private const string HostingStartupVariable = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";

    /// <summary>
    /// In a watched run, a program using ASP.NET Core also references Piper.Watch (beside piper), which ASP.NET Core then
    /// loads as a hosting startup; otherwise the references unchanged.
    /// </summary>
    public static ProgramReferences WithBrowserReload(ProgramReferences references)
    {
        var reload = Path.Combine(AppContext.BaseDirectory, "Piper.Watch.dll");
        if (Environment.GetEnvironmentVariable(ReloadVariable) is null || !File.Exists(reload) ||
            !references.Frameworks.Any(f => f.Name == "Microsoft.AspNetCore.App"))
            return references;
        return references with { Assemblies = references.Assemblies.Add(reload) };
    }

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
        // The projects a folder shares code with are watched too, as a change to them changes the program.
        var watched = inputs.Concat(inputs.Where(Directory.Exists).SelectMany(input => Piper.LoadedProject.SharedFoldersOf(input))).Distinct().ToList();
        var watchers = watched.Select(input => Watcher(input, path =>
        {
            lastChange = path;
            changed.Release();
        })).ToList();

        using var pages = new ReloadEvents();
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
            var restarted = false;
            while (!stopping)
            {
                child = Start(inputs, programArguments, pages.Url);
                if (restarted)
                    pages.Reload();
                restarted = true;
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
    private static Process Start(IReadOnlyList<string> inputs, string[] programArguments, string reloadUrl)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.Environment[WatcherVariable] = Environment.ProcessId.ToString();
        start.Environment[ReloadVariable] = reloadUrl;
        var startups = Environment.GetEnvironmentVariable(HostingStartupVariable);
        start.Environment[HostingStartupVariable] = string.IsNullOrEmpty(startups) ? "Piper.Watch" : $"{startups};Piper.Watch";
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

    /// <summary>
    /// The event stream pages listen to: server-sent events on a free loopback port, answering any request with a stream
    /// that stays open, and sending "reload" down every open one when <see cref="Reload"/> is called. A plain socket, so
    /// it needs no URL reservation on Windows and nothing beyond the platform.
    /// </summary>
    private sealed class ReloadEvents : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<NetworkStream> _pages = [];
        private readonly Lock _lock = new();

        public ReloadEvents()
        {
            _listener.Start();
            Url = $"http://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = AcceptAsync();
        }

        public string Url { get; }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception e) when (e is ObjectDisposedException or SocketException)
                {
                    return;
                }
                _ = OpenAsync(client);
            }
        }

        /// <summary>Reads the request's headers, then answers with an event stream (open to any page's origin) and keeps it.</summary>
        private async Task OpenAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new byte[4096];
                var read = 0;
                while (read < request.Length && !Encoding.ASCII.GetString(request, 0, read).Contains("\r\n\r\n"))
                {
                    var count = await stream.ReadAsync(request.AsMemory(read));
                    if (count == 0)
                        return;
                    read += count;
                }
                const string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n" +
                                       "Access-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n: watching\n\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
                lock (_lock)
                    _pages.Add(stream);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
                client.Dispose();
            }
        }

        /// <summary>Tells every open page to reload, and forgets them: each reconnects when it loads again.</summary>
        public void Reload()
        {
            List<NetworkStream> pages;
            lock (_lock)
            {
                pages = [.. _pages];
                _pages.Clear();
            }
            var message = Encoding.ASCII.GetBytes("data: reload\n\n");
            foreach (var page in pages)
            {
                try
                {
                    page.Write(message);
                    page.Flush();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
                finally
                {
                    page.Dispose();
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_lock)
            {
                foreach (var page in _pages)
                    page.Dispose();
                _pages.Clear();
            }
        }
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
