using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// Persisted actors' events in a database (Piper.Data's DatabaseEventStore), against SQLite files: they outlive the
/// store that wrote them, as they'd outlive a process, two writers of one stream are caught, and the table is made
/// beside a program's own in a database that already has them.
/// </summary>
[Collection("data plugin")]
public class EventStoreTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll", "Piper.Plugins.Actors/bin/plugin/Piper.Plugins.Actors.dll",
             "Piper.Plugins.Hosting/bin/plugin/Piper.Plugins.Hosting.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-data-tests"), packages, [], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    [Fact]
    public void A_persisted_actors_events_outlive_the_store_that_wrote_them()
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-events-{Guid.NewGuid():N}.db");
        try
        {
            var result = Compilation.Compile([new SourceText($$"""
                using System;
                using Microsoft.Data.Sqlite;
                using Microsoft.EntityFrameworkCore;
                using Piper.Data;
                using Piper.Runtime;

                actor Tally(id: string) persisted by id {
                    mut total = 0;
                    event Added(n: int) { total += n; }
                    on Add(n: int) => int { emit Added(n); total }
                    on Total() => int { total }
                }

                async fn Main() {
                    let connection = "Data Source={{path.Replace("\\", "\\\\")}};Pooling=False";
                    // The program's own table, already there: the events' table is made beside it.
                    {
                        using let sqlite = SqliteConnection(connection);
                        sqlite.Open();
                        using let command = sqlite.CreateCommand();
                        command.CommandText = "create table Orders (Id integer primary key)";
                        command.ExecuteNonQuery();
                    }
                    let options = DbContextOptionsBuilder().UseSqlite(connection).Options;

                    let first = Tally.Spawn(await DatabaseEventStore.OpenAsync(options), "visits");
                    Print(await first.Add(2));
                    Print(await first.Add(5));
                    await first.Stop();

                    // As a new process would: a new store on the same database.
                    let store = await DatabaseEventStore.OpenAsync(options);
                    let again = Tally.Spawn(store, "visits");
                    Print(await again.Total());
                    Print(await Tally.Spawn(store, "other").Total());

                    // Two actors writing one stream: the second's save is refused, and saves nothing.
                    let rival = Tally.Spawn(store, "visits");
                    Print(await rival.Total());
                    Print(await again.Add(1));
                    try { await rival.Add(100); } catch (e: EventStreamConflictException) { Print("conflict"); }
                    Print(await Tally.Spawn(store, "visits").Total());
                }
                """, "main.piper")],
                new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            var run = Runner.Run(result.Files, references: result.References);
            Assert.True(run.Success, string.Join("\n", run.Errors));
            Assert.Equal("2\n7\n7\n0\n7\n8\nconflict\n8\n", run.Output.Replace("\r\n", "\n"));
        }
        finally
        {
            // Pooling is off, so the file is closed once the program ends.
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A provider chosen by configuration: a host's `provider store: IEventStore from "Events";` reads Events:Provider
    /// and its settings (here from a source standing in for appsettings.json or a vault), makes that store and registers
    /// it, so the same program keeps its events in memory, a SQLite file or a cloud database by configuration alone.
    /// With nothing configured it's the local store; a name that isn't a provider stops it, listing the names there are.
    /// </summary>
    [Fact]
    public void Configuration_chooses_the_event_store_at_startup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-events-{Guid.NewGuid():N}.db");
        try
        {
            var result = Compilation.Compile([new SourceText($$"""
                using System;
                using System.Threading;
                using Microsoft.Extensions.Configuration;
                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.Hosting;
                using Piper.Runtime;

                actor Tally(id: string) persisted by id {
                    mut total = 0;
                    event Added(n: int) { total += n; }
                    on Add(n: int) => int { emit Added(n); total }
                }

                fn Settings(configuration: IConfigurationBuilder, json: string) => IConfigurationBuilder =
                    configuration.AddJsonStream(System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

                host Node(json: string) {
                    logging off;
                    sources (configuration: IConfigurationBuilder) => Settings(configuration, json);
                    provider store: IEventStore from "Events";
                }

                fn Store(json: string) => IEventStore = Node(json).Services.GetRequiredService<IEventStore>();

                async fn Main() {
                    let file = "{ \"Events\": { \"Provider\": \"sqlite\", \"Connection\": \"Data Source={{path.Replace("\\", "/")}};Pooling=False\" } }";
                    let first = Tally.Spawn(Store(file), "visits");
                    Print(await first.Add(2));
                    await first.Stop();
                    Print(await Tally.Spawn(Store(file), "visits").Add(3));

                    Print(Store("{}") is _: MemoryEventStore);
                    try {
                        Store("{ \"Events\": { \"Provider\": \"floppy\" } }");
                    } catch (e: ProviderException) {
                        Print(e.Message);
                    }
                    try {
                        Store("{ \"Events\": { \"Provider\": \"sqlite\" } }");
                    } catch (e: ProviderException) {
                        Print(e.Message);
                    }
                }
                """, "main.piper")],
                new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            var run = Runner.Run(result.Files, references: result.References);
            Assert.True(run.Success, string.Join("\n", run.Errors));
            Assert.Equal(
                "2\n5\nTrue\n" +
                "Events:Provider is 'floppy', which isn't a provider of IEventStore: it can be memory, postgres, sqlite, sqlserver. " +
                "A provider comes with the plugin that brings it, in piper.json.\n" +
                "Events:Connection isn't set, and the sqlite provider needs it.\n",
                run.Output.Replace("\r\n", "\n"));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
