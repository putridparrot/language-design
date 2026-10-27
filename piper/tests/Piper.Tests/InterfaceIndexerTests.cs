namespace Piper.Tests;

/// <summary>
/// A class implementing .NET interface indexers whose result or parameters .NET marks as possibly null: Options in
/// Piper, converted by an explicit implementation. The interfaces come from a library compiled from C#.
/// </summary>
public sealed class InterfaceIndexerTests : IDisposable
{
    private readonly LibraryPrograms _programs = new();

    public void Dispose() => _programs.Dispose();

    private const string Library = """
        namespace Stores;
        // A result that may be null, settable: a reference type and a value type.
        public interface INames { string? this[string key] { get; set; } }
        public interface IScores { int? this[string key] { get; } }
        // A parameter that may be null, and one that can't be, with a plain result.
        public interface ILabels { string this[string? key, int index] { get; } }
        public static class Use
        {
            public static string Names(INames names)
            {
                names["b"] = "Bea";
                names["a"] = null;
                return $"{names["a"] ?? "none"} {names["b"]}";
            }
            public static string Scores(IScores scores) => $"{scores["x"]} {scores["y"]?.ToString() ?? "none"}";
            public static string Labels(ILabels labels) => $"{labels[null, 1]} {labels["k", 2]}";
        }
        """;

    private const string Program = """
        using System.Collections.Generic;
        using Stores;

        class Names: INames {
            let values = Dictionary<string, string>();
            prop this[key: string] => string? {
                get = if values.TryGetValue(key, out let found) { Some(found) } else { None };
                set {
                    match value {
                        Some(v) => values[key] = v,
                        None => { values.Remove(key); },
                    }
                }
            }
        }

        class Scores: IScores {
            prop this[key: string] => int? {
                get = if key == "x" { Some(7) } else { None };
            }
        }

        class Labels: ILabels {
            prop this[key: string?, index: int] => string {
                get = $"{key ?? "default"}#{index}";
            }
        }

        fn Main() {
            let names = Names();
            names["a"] = Some("Ada");
            Print(Use.Names(names));
            Print(names["a"] ?? "gone");
            Print(Use.Scores(Scores()));
            Print(Use.Labels(Labels()));
            // Piper code uses the class's own indexer, of Options.
            Print(Labels()[None, 3]);
        }
        """;

    [Fact]
    public void Indexers_of_options_implement_interface_indexers_that_may_take_or_give_null()
    {
        var result = _programs.Compile("Stores", Library, Program);
        Assert.Equal("none Bea\ngone\n7 none\ndefault#1 k#2\ndefault#3\n", LibraryPrograms.Run(result));
    }

    [Theory]
    [InlineData("class Bad: INames { prop this[key: string] => string { get = key; set { } } } fn Main() { }")]
    [InlineData("class Bad: INames { prop this[key: string] => string? { get = None; } } fn Main() { }")]
    [InlineData("class Bad: ILabels { prop this[key: string, index: int] => string { get = key; } } fn Main() { }")]
    public void An_indexer_must_use_options_where_dotnet_may_give_or_take_null(string program)
    {
        var result = _programs.Compile("Stores", Library, "using Stores;\n" + program);
        Assert.Contains(result.Diagnostics, d => d.Code is "PIP0245" or "PIP0246");
        Assert.False(result.Success);
    }
}
