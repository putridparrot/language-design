using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Runtime;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The stores behind idempotent endpoints: <see cref="MemoryIdempotencyStore"/>, and Piper.Data's
/// <c>DatabaseIdempotency</c> (the data plugin's <c>idempotency;</c>), which must agree. What the endpoints do with them
/// is in WebPluginTests.
/// </summary>
public class IdempotencyTests
{
    private static readonly IdempotentResponse Created = new(201, "application/json", "/issues/1", "{\"id\":1}"u8.ToArray());

    [Fact]
    public async Task The_memory_store_runs_a_key_once_and_answers_its_repeats()
    {
        var store = new MemoryIdempotencyStore();
        var lease = TimeSpan.FromMinutes(1);

        Assert.Equal(IdempotencyState.Claimed, (await store.ClaimAsync("k", "a", lease)).State);
        Assert.Equal(IdempotencyState.Busy, (await store.ClaimAsync("k", "a", lease)).State);
        Assert.Equal(IdempotencyState.Mismatch, (await store.ClaimAsync("k", "b", lease)).State);

        await store.CompleteAsync("k", Created, TimeSpan.FromHours(1));
        var done = await store.ClaimAsync("k", "a", lease);
        Assert.Equal(IdempotencyState.Done, done.State);
        Assert.Equal((201, "/issues/1", "{\"id\":1}"), (done.Response!.Status, done.Response.Location, System.Text.Encoding.UTF8.GetString(done.Response.Body)));
        // Released after its response is kept, it stays: only a request that failed lets its key go.
        await store.ReleaseAsync("k");
        Assert.Equal(IdempotencyState.Done, (await store.ClaimAsync("k", "a", lease)).State);

        Assert.Equal(IdempotencyState.Claimed, (await store.ClaimAsync("failed", "a", lease)).State);
        await store.ReleaseAsync("failed");
        Assert.Equal(IdempotencyState.Claimed, (await store.ClaimAsync("failed", "a", lease)).State);
    }

    [Fact]
    public void The_memory_store_lets_a_key_go_when_its_time_is_up()
    {
        new PiperSimulation().Run(async () =>
        {
            var store = new MemoryIdempotencyStore();
            Assert.Equal(IdempotencyState.Claimed, (await store.ClaimAsync("k", "a", TimeSpan.FromMinutes(2))).State);
            await Task.Delay(TimeSpan.FromMinutes(1), PiperRuntime.Current.Time);
            Assert.Equal(IdempotencyState.Busy, (await store.ClaimAsync("k", "a", TimeSpan.FromMinutes(2))).State);
            // The request holding it never finished (its process died): after its lease, a repeat runs.
            await Task.Delay(TimeSpan.FromMinutes(2), PiperRuntime.Current.Time);
            Assert.Equal(IdempotencyState.Claimed, (await store.ClaimAsync("k", "b", TimeSpan.FromMinutes(2))).State);

            await store.CompleteAsync("k", Created, TimeSpan.FromHours(24));
            await Task.Delay(TimeSpan.FromHours(25), PiperRuntime.Current.Time);
            Assert.Equal(IdempotencyState.Claimed, (await store.ClaimAsync("k", "c", TimeSpan.FromMinutes(2))).State);
        });
    }

    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Data = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-outbox-tests"), packages,
            [.. plugins.SelectMany(p => p.Frameworks).Distinct()], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    /// <summary>The database's store, two of them sharing a table as two pods would, behaves as the memory one.</summary>
    [Fact]
    public void The_database_store_runs_a_key_once_across_pods_and_answers_its_repeats()
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-idempotency-{Guid.NewGuid():N}.db");
        try
        {
            var result = Compilation.Compile([new SourceText($$"""
                using System;
                using System.Text;
                using Microsoft.EntityFrameworkCore;
                using Piper.Data;
                using Piper.Runtime;

                database Shop {
                    idempotency;
                }

                fn Open() => Shop = Shop(DbContextOptionsBuilder<Shop>().UseSqlite("Data Source={{path.Replace("\\", "/")}};Pooling=False").Options);

                async fn Main() {
                    {
                        using let shop = Open();
                        shop.Database.EnsureCreated();
                    }
                    let one = DatabaseIdempotency.Store(() => Open());
                    let two = DatabaseIdempotency.Store(() => Open());
                    let lease = TimeSpan.FromMinutes(1);

                    Print((await one.ClaimAsync("k", "a", lease)).State);
                    Print((await two.ClaimAsync("k", "a", lease)).State);
                    Print((await two.ClaimAsync("k", "b", lease)).State);
                    await one.CompleteAsync("k", IdempotentResponse(201, "application/json", "/issues/1", Encoding.UTF8.GetBytes("{}")), TimeSpan.FromHours(1));
                    let done = await two.ClaimAsync("k", "a", lease);
                    Print($"{done.State} {done.Response?.Status} {done.Response?.Location} {Encoding.UTF8.GetString(done.Response?.Body ?? [])}");

                    Print((await one.ClaimAsync("failed", "a", lease)).State);
                    await one.ReleaseAsync("failed");
                    Print((await two.ClaimAsync("failed", "a", lease)).State);

                    // A lease already up (a request whose pod died): the next request takes the key over.
                    Print((await one.ClaimAsync("dead", "a", TimeSpan.FromSeconds(-5))).State);
                    Print((await two.ClaimAsync("dead", "b", lease)).State);
                }
                """, "main.piper")],
                new CompilationOptions { Plugins = Data.Value.Plugins, References = Data.Value.References });
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            var run = Runner.Run(result.Files, references: result.References);
            Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
            Assert.Equal("Claimed\nBusy\nMismatch\nDone 201 /issues/1 {}\nClaimed\nClaimed\nClaimed\nClaimed\n", run.Output.Replace("\r\n", "\n"));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
