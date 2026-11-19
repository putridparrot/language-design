using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// A server's or host's <c>database</c> clause: the database declaration's class on the provider and connection the
/// configuration chooses (<c>from "Database"</c>), or the program's own settings give (<c>(provider, connection)</c>),
/// registered as a scoped service, with a name for the clauses after. A provider Piper doesn't know, or a missing
/// connection, stops the program as it starts. apps/tracker's server uses the second form.
/// </summary>
[Collection(nameof(DatabaseClauseTests))]
public class DatabaseClauseTests
{
    // The tests set environment variables, which the host reads: they run alone.
    [CollectionDefinition(nameof(DatabaseClauseTests), DisableParallelization = true)]
    public sealed class RunsAlone;

    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll", "Piper.Plugins.Hosting/bin/plugin/Piper.Plugins.Hosting.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-database-clause-tests"), packages,
            [.. plugins.SelectMany(p => p.Frameworks).Distinct()], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private const string Program = """
        using System;
        using System.Linq;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        record Item(id: int, name: string);

        database Shop {
            table Items: Item key id;
        }

        host FromSection {
            logging off;
            database shop: Shop from "Database";
            singleton Func<Shop> = shop;
        }

        host FromSettings(provider: string, connection: string) {
            logging off;
            database Shop(provider, connection);
        }

        fn Main() {
            using let first = FromSection();
            let open = first.Services.GetRequiredService<Func<Shop>>();
            {
                using let shop = open();
                shop.Database.EnsureCreated();
                shop.Items.Add(Item(1, "kettle"));
                shop.SaveChanges();
            }
            // Scoped: one per scope, the same within it.
            using let scope = first.Services.CreateScope();
            let a = scope.ServiceProvider.GetRequiredService<Shop>();
            Print($"{a.Database.ProviderName ?? ""} {a.Items.Count()} {object.ReferenceEquals(a, scope.ServiceProvider.GetRequiredService<Shop>())}");
            using let other = first.Services.CreateScope();
            Print(object.ReferenceEquals(a, other.ServiceProvider.GetRequiredService<Shop>()));

            using let second = FromSettings("sqlite", Environment.GetEnvironmentVariable("Database__Connection") ?? "");
            using let again = second.Services.CreateScope();
            Print(again.ServiceProvider.GetRequiredService<Shop>().Items.Single().name);
        }
        """;

    private static (bool Success, string Output, string Errors) Run(params (string Name, string? Value)[] environment)
    {
        var result = Compilation.Compile([new SourceText(Program, "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        foreach (var (name, value) in environment)
            Environment.SetEnvironmentVariable(name, value);
        try
        {
            var run = Runner.Run(result.Files, references: result.References);
            return (run.Success, run.Output.Replace("\r\n", "\n"), string.Join("\n", run.Errors));
        }
        finally
        {
            foreach (var (name, _) in environment)
                Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void A_database_comes_from_the_configuration_or_the_programs_settings_and_is_scoped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-database-{Guid.NewGuid():N}.db");
        try
        {
            var (success, output, errors) = Run(("Database__Connection", $"Data Source={path};Pooling=False"));
            Assert.True(success, errors + "\n" + output);
            Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite 1 True\nFalse\nkettle\n", output);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(null, null, "Database:Connection isn't set")]
    [InlineData("oracle", "Data Source=x", "Database:Provider is 'oracle', which isn't a database Piper knows: sqlite, sqlserver, postgres.")]
    public void A_database_that_cant_be_made_stops_the_program_as_it_starts(string? provider, string? connection, string message)
    {
        var (success, output, errors) = Run(("Database__Provider", provider), ("Database__Connection", connection));
        Assert.False(success, output);
        Assert.Contains(message, errors + output);
    }
}
