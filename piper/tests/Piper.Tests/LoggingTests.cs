using System.Collections.Immutable;
using System.Text.Json;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// Structured logging from interpolation: a <c>$"…"</c> given to ILogger's <c>Log…</c> methods is a template with a named
/// field for each hole, and as a statement checks the level first, so a disabled level evaluates no hole.
/// Microsoft.Extensions.Logging comes from ASP.NET Core's shared framework, so these run offline.
/// </summary>
public class LoggingTests
{
    private static readonly Lazy<ProgramReferences> References = new(() =>
    {
        var (references, errors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, ["Microsoft.AspNetCore.App"], []);
        Assert.Empty(errors);
        return references;
    });

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { References = References.Value });

    private const string Setup = """
        using System;
        using Microsoft.Extensions.Logging;

        record Order(id: int, total: decimal);

        class Counter { public mut evaluated = 0; }

        fn Counted(counter: Counter, value: int) => int {
            counter.evaluated += 1;
            value
        }

        """;

    /// <summary>Each line the JSON console logger wrote: its "Message", and its fields, in "State".</summary>
    private static List<JsonElement> Lines(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('{'))
            .Select(line => JsonDocument.Parse(line).RootElement)];

    [Fact]
    public void An_interpolated_message_is_a_template_with_named_fields()
    {
        var result = Compile(Setup + """
            fn Main() {
                let counter = Counter();
                using let factory = LoggerFactory.Create(b => b.AddJsonConsole().SetMinimumLevel(LogLevel.Information));
                let log = factory.CreateLogger("shop");
                let quantity = 3;
                let sku = "tea";
                let order = Order(7, 9.5m);
                log.LogInformation($"Reserved {quantity} of {sku}");
                log.LogWarning(InvalidOperationException("boom"), $"Order {order.id} came to {order.total:F2} {{gross}}");
                log.LogInformation($"Counted {Counted(counter, 5)} and {quantity} and {quantity}");
                log.LogDebug($"Skipped {Counted(counter, 6)}");
                log.LogInformation("plain");
                Print($"evaluated {counter.evaluated}");
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Contains("if (log.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))", result.CSharp);
        Assert.Contains("\"Reserved {quantity} of {sku}\", quantity, sku", result.CSharp);

        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        // The Debug line's hole wasn't evaluated: only the Information line's was.
        Assert.Contains("evaluated 1", run.Output);

        var lines = Lines(run.Output);
        Assert.Equal(4, lines.Count);
        var reserved = lines[0].GetProperty("State");
        Assert.Equal("Reserved 3 of tea", lines[0].GetProperty("Message").GetString());
        Assert.Equal("Reserved {quantity} of {sku}", reserved.GetProperty("{OriginalFormat}").GetString());
        Assert.Equal(3, reserved.GetProperty("quantity").GetInt32());
        Assert.Equal("tea", reserved.GetProperty("sku").GetString());

        var order = lines[1].GetProperty("State");
        Assert.Equal("Order 7 came to 9.50 {gross}", lines[1].GetProperty("Message").GetString());
        Assert.Equal(7, order.GetProperty("order.id").GetInt32());
        Assert.Contains("boom", lines[1].GetProperty("Exception").GetString());

        // Fields named as their holes, a call by its function, and a name used twice numbered.
        var counted = lines[2].GetProperty("State");
        Assert.Equal("Counted {Counted} and {quantity} and {quantity2}", counted.GetProperty("{OriginalFormat}").GetString());
        Assert.Equal("plain", lines[3].GetProperty("Message").GetString());
    }

    /// <summary>
    /// A logger that isn't a plain variable or field would be evaluated twice by the level check, so it gets the template
    /// without the check; so does a call that isn't a statement, and <c>Log(level, …)</c>, whose level is an argument.
    /// </summary>
    [Fact]
    public void Only_a_plain_logger_in_a_statement_is_checked_first()
    {
        var result = Compile(Setup + """
            fn Main() {
                using let factory = LoggerFactory.Create(b => b.AddJsonConsole());
                let sku = "tea";
                factory.CreateLogger("a").LogInformation($"Made {sku}");
                let log = factory.CreateLogger("b");
                let later = () => log.LogInformation($"Later {sku}");
                later();
                log.Log(LogLevel.Warning, $"Levelled {sku}");
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.DoesNotContain("IsEnabled", result.CSharp);
        Assert.Contains("\"Made {sku}\", sku", result.CSharp);
        Assert.Contains("\"Later {sku}\", sku", result.CSharp);
        Assert.Contains("\"Levelled {sku}\", sku", result.CSharp);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal(["Made tea", "Later tea", "Levelled tea"], Lines(run.Output).Select(l => l.GetProperty("Message").GetString()!).ToArray());
    }
}
