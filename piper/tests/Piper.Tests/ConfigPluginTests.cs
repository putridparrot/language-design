using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The config plugin (plugins/Piper.Plugins.Config): `config` declarations, read from an IConfiguration. The plugin is
/// loaded from its build output; configuration comes from ASP.NET Core's shared framework, so these tests run offline
/// without restoring the plugin's packages.
/// </summary>
public class ConfigPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Config = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Config/bin/plugin/Piper.Plugins.Config.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, ["Microsoft.AspNetCore.App"], []);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Config.Value.Plugins, References = Config.Value.References });

    private static string Run(string text)
    {
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }

    /// <summary>The configs under test, and `Settings(…)`, making a configuration from key-value pairs.</summary>
    private const string Smtp = """
        using System;
        using System.Collections.Generic;
        using Microsoft.Extensions.Configuration;

        config Tls {
            enabled: bool = true;
            certificate: string?;
        }

        config Smtp from "Mail" {
            host: string;
            port: int = 587 requires port > 0 && port < 65536;
            user: string?;
            timeout: TimeSpan = TimeSpan.FromSeconds(30);
            recipients: List<string>;
            ports: List<int> = [25];
            tls: Tls;
        }

        fn Settings(values: Dictionary<string, string>) => IConfiguration {
            let configuration = ConfigurationManager();
            for (key, value) in values { configuration[key] = value; }
            configuration
        }

        fn Report(result: Result<Smtp, List<string>>) {
            match result {
                Ok(s) => Print($"{s.host} {s.port} {s.user} {s.timeout} [{string.Join(", ", s.recipients)}] [{string.Join(", ", s.ports)}] {s.tls.enabled} {s.tls.certificate}"),
                Err(problems) => { for problem in problems { Print(problem); } },
            }
        }

        """;

    [Fact]
    public void A_config_reads_its_section_with_defaults_and_nested_sections()
    {
        var output = Run(Smtp + """
            fn Main() {
                Report(LoadSmtp(Settings([
                    "Mail:Host": "smtp.example", "MAIL:PORT": "2525", "Mail:User": "ada",
                    "Mail:Recipients:0": "a@example", "Mail:Recipients:1": "b@example",
                    "Mail:Tls:Certificate": "cert.pem",
                ])));
                Report(LoadSmtp(Settings(["Mail:Host": "smtp.example", "Mail:Timeout": "00:01:00", "Mail:Ports:0": "465", "Mail:Ports:1": "587"])));
            }
            """);
        Assert.Equal("""
            smtp.example 2525 Some(ada) 00:00:30 [a@example, b@example] [25] True Some(cert.pem)
            smtp.example 587 None 00:01:00 [] [465, 587] True None

            """, output);
    }

    /// <summary>
    /// The record a config becomes keeps its defaults, so code making one directly (a test's) can leave them out or name
    /// them, and adding a field with a default doesn't break those calls. An optional field without one is None by
    /// default. A record's defaults come after the fields without them, so a field whose default isn't a literal (or
    /// that has none) ends the run: those before it are required.
    /// </summary>
    [Fact]
    public void A_configs_record_keeps_its_defaults_for_code_that_makes_one()
    {
        var output = Run("""
            using System;

            config Server from "Server" {
                host: string;
                timeout: TimeSpan = TimeSpan.FromSeconds(30);
                port: int = 8080;
                retries: int = -1;
                secret key: string?;
                name: string = "piper";
            }

            fn Main() {
                Print(Server("a", TimeSpan.Zero));
                Print(Server("b", TimeSpan.Zero, name: "named"));
                Print(Server("c", TimeSpan.Zero, 1, 2, Some(Piper.Runtime.Secret<string>("k")), "all"));
            }
            """);
        Assert.Equal("""
            Server { host = a, timeout = 00:00:00, port = 8080, retries = -1, key = None, name = piper }
            Server { host = b, timeout = 00:00:00, port = 8080, retries = -1, key = None, name = named }
            Server { host = c, timeout = 00:00:00, port = 1, retries = 2, key = Some(***), name = all }

            """, output);
    }

    /// <summary>
    /// A secret field is read as its type and kept as a Secret: printing the config shows ***, and .Reveal() gives the
    /// value. A missing optional one is None; a missing required one is reported as any field is.
    /// </summary>
    [Fact]
    public void A_secret_field_is_read_and_never_shown()
    {
        var output = Run("""
            using System;
            using System.Collections.Generic;
            using Microsoft.Extensions.Configuration;

            config Database {
                host: string;
                secret password: string;
                secret token: string?;
                secret pin: int?;
            }

            fn Settings(values: Dictionary<string, string>) => IConfiguration {
                let configuration = ConfigurationManager();
                for (key, value) in values { configuration[key] = value; }
                configuration
            }

            fn Main() {
                match LoadDatabase(Settings(["Database:Host": "db", "Database:Password": "hunter2", "Database:Pin": "1234"])) {
                    Ok(d) => {
                        Print(d);
                        Print(d.password.Reveal());
                        Print(d.token is None);
                        Print(d.pin?.Reveal() ?? 0);
                    },
                    Err(problems) => Print(string.Join("; ", problems)),
                }
                match LoadDatabase(Settings(["Database:Host": "db"])) {
                    Ok(_) => Print("loaded"),
                    Err(problems) => Print(string.Join("; ", problems)),
                }
            }
            """);
        Assert.Equal("""
            Database { host = db, password = ***, token = None, pin = Some(***) }
            hunter2
            True
            1234
            Database:password is missing

            """, output);
    }

    [Fact]
    public void A_secret_is_one_value()
    {
        var result = Compile("config C { secret keys: System.Collections.Generic.List<string>; }\nfn Main() {}");
        Assert.Contains(result.FormattedDiagnostics, d => d.Contains("A secret is one value"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once_with_its_key()
    {
        var output = Run(Smtp + """
            fn Main() {
                Report(LoadSmtp(Settings(["Mail:Port": "abc", "Mail:Ports:0": "x", "Mail:Tls:Enabled": "maybe"])));
                Report(LoadSmtp(Settings(["Mail:Host": "h", "Mail:Port": "0"])));
            }
            """);
        Assert.Equal("""
            Mail:host is missing
            Mail:port: "abc" isn't a valid int
            Mail:ports:0: "x" isn't a valid int
            Mail:tls:enabled: "maybe" isn't a valid bool
            Mail: Invariant failed: port > 0 && port < 65536 (port = 0)

            """, output);
    }

    [Fact]
    public void ConfigFrom_throws_with_every_problem()
    {
        var output = Run(Smtp + """
            fn Main() {
                try {
                    let smtp = SmtpFrom(Settings(["Mail:Host": "h", "Mail:Port": "-1"]));
                } catch (e: InvalidOperationException) {
                    Print(e.Message);
                }
            }
            """);
        Assert.Equal("""
            The configuration has problems:
            Mail: Invariant failed: port > 0 && port < 65536 (port = -1)

            """, output);
    }

    [Fact]
    public void A_field_of_a_type_configuration_cant_hold_is_reported()
    {
        var text = """
            config Bad {
                when: System.DateTimeOffset;
                when: int;
            }
            fn Main() { }
            """;
        var errors = Compile(text).Diagnostics;
        Assert.Equal(["PIP0503"], errors.Select(e => e.Code).Distinct());
        Assert.Contains(errors, e => e.Message.Contains("can't be read as this type") && e.Span.Start == text.IndexOf("System.DateTimeOffset", StringComparison.Ordinal));
    }

    [Fact]
    public void A_field_given_twice_is_reported()
    {
        var text = """
            config Twice {
                port: int = 1;
                port: int = 2;
            }
            fn Main() { }
            """;
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Contains("'port' is already a field of Twice", error.Message);
        Assert.Equal(text.LastIndexOf("port", StringComparison.Ordinal), error.Span.Start);
    }
}
