using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// A cluster's members kept as leases in a database the nodes share (Piper.Data's DatabaseClusterMembership), against
/// SQLite files: nodes find each other through it with no seeds, a node that leaves gives up its lease, one that stops
/// renewing drops out once its lease runs out, and configuration chooses it as it does the event store.
/// </summary>
[Collection("data plugin")]
public class ClusterMembershipTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll",
             "Piper.Plugins.Actors.Remote/bin/plugin/Piper.Plugins.Actors.Remote.dll",
             "Piper.Plugins.Hosting/bin/plugin/Piper.Plugins.Hosting.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-membership-tests"), packages,
            [.. plugins.SelectMany(p => p.Frameworks).Distinct()], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static string Run(string program)
    {
        var result = Compilation.Compile([new SourceText(program, "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }

    private static void WithDatabase(Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-members-{Guid.NewGuid():N}.db");
        try
        {
            test($"Data Source={path.Replace("\\", "/")};Pooling=False");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void Leases_are_taken_given_up_and_run_out() => WithDatabase(connection =>
        Assert.Equal("a, b\na\na\n\n", Run($$"""
            using System;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using Piper.Data;

            async fn Main() {
                let options = DbContextOptionsBuilder().UseSqlite("{{connection}}").Options;
                let lease = TimeSpan.FromMilliseconds(600);
                let first = await DatabaseClusterMembership.OpenAsync(options, lease);
                await first.JoinAsync("shop", "a");
                await first.JoinAsync("shop", "b");
                await first.JoinAsync("elsewhere", "c");
                Print(string.Join(", ", await first.MembersAsync("shop")));
                await first.LeaveAsync("shop", "b");
                Print(string.Join(", ", await first.MembersAsync("shop")));

                // Another process, opening the same database, sees the same members.
                let second = await DatabaseClusterMembership.OpenAsync(options, lease);
                Print(string.Join(", ", await second.MembersAsync("shop")));

                // a stops renewing (its process gone): once its lease runs out, it isn't listed.
                await Task.Delay(TimeSpan.FromMilliseconds(900));
                Print(string.Join(", ", await second.MembersAsync("shop")));
            }
            """)));

    /// <summary>
    /// Leases are timed by the database's clock, so nodes whose clocks are an hour apart still see each other's: by their
    /// own clocks, the slow one's lease would have run out as it was taken, and the fast one would see no lease at all.
    /// </summary>
    [Fact]
    public void Leases_are_timed_by_the_databases_clock_not_the_nodes() => WithDatabase(connection =>
        Assert.Equal("fast, slow\nfast, slow\n", Run($$"""
            using System;
            using Microsoft.EntityFrameworkCore;
            using Piper.Data;

            class Skewed(by: TimeSpan): TimeProvider {
                override fn GetUtcNow() => DateTimeOffset = DateTimeOffset.UtcNow + by;
            }

            async fn Main() {
                let options = DbContextOptionsBuilder().UseSqlite("{{connection}}").Options;
                let lease = TimeSpan.FromSeconds(30);
                let slow = await DatabaseClusterMembership.OpenAsync(options, lease, Skewed(TimeSpan.FromHours(-1)));
                let fast = await DatabaseClusterMembership.OpenAsync(options, lease, Skewed(TimeSpan.FromHours(1)));
                await slow.JoinAsync("shop", "slow");
                await fast.JoinAsync("shop", "fast");
                Print(string.Join(", ", await slow.MembersAsync("shop")));
                Print(string.Join(", ", await fast.MembersAsync("shop")));
            }
            """)));

    [Fact]
    public void Nodes_find_each_other_through_the_database_with_no_seeds() => WithDatabase(connection =>
        Assert.Equal("True\nTrue\nTrue\n2\nTrue\n", Run($$"""
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using Piper.Actors.Remote;
            using Piper.Data;
            using Piper.Runtime;

            async fn Members(node: ActorSystem, count: int) => Task<bool> {
                let deadline = DateTime.UtcNow.AddSeconds(15);
                while node.Cluster.Members.Count != count && DateTime.UtcNow < deadline {
                    await Task.Delay(20);
                }
                node.Cluster.Members.Count == count
            }

            // Each node as its own process would: its own membership, on the shared database.
            async fn Join(options: DbContextOptions) => Task<ActorSystem> {
                let node = await PiperNode.StartInMemoryAsync();
                let membership = await DatabaseClusterMembership.OpenAsync(options, TimeSpan.FromSeconds(1));
                await Cluster.JoinAsync(node, membership, "shop", ClusterOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
                node
            }

            async fn Main() {
                let options = DbContextOptionsBuilder().UseSqlite("{{connection}}").Options;
                let a = await Join(options);
                let b = await Join(options);
                let c = await Join(options);
                Print(await Members(a, 3) && await Members(b, 3) && await Members(c, 3));

                // b leaves: the others stop counting it, and its lease is gone.
                await b.StopAsync();
                Print(await Members(a, 2) && await Members(c, 2));
                let reader = await DatabaseClusterMembership.OpenAsync(options);
                Print(!(await reader.MembersAsync("shop")).Contains(b.Address));
                Print((await reader.MembersAsync("shop")).Count);

                // A node joining later finds them through the database too.
                let d = await Join(options);
                Print(await Members(d, 3) && await Members(a, 3));
                await a.StopAsync();
                await c.StopAsync();
                await d.StopAsync();
            }
            """)));

    [Fact]
    public void Configuration_chooses_a_database_membership() => WithDatabase(connection =>
        Assert.Equal("True\nTrue\nCluster:Lease must be a number of seconds, not 'soon'.\n", Run($$"""
            using Microsoft.Extensions.Configuration;
            using Piper.Data;
            using Piper.Hosting;
            using Piper.Runtime;

            // As appsettings.json would give them, as apps/stock reads its configuration.
            fn Membership(lease: string) => IClusterMembership {
                let json = "{ \"Cluster\": { \"Provider\": \"sqlite\", \"Connection\": \"{{connection}}\", \"Lease\": \"" + lease + "\" } }";
                let configuration = ConfigurationBuilder().AddJsonStream(System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
                PiperHost.Provider<IClusterMembership>(configuration, "Cluster")
            }

            fn Main() {
                let chosen = Membership("10");
                Print(chosen is _: DatabaseClusterMembership);
                match chosen {
                    database: DatabaseClusterMembership => Print(database.Lease.TotalSeconds == 10),
                    _ => Print(false),
                }
                try {
                    Membership("soon");
                } catch (e: ProviderException) {
                    Print(e.Message);
                }
            }
            """)));
}
