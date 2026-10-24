using System.Text;
using System.Text.Json.Nodes;
using Piper.LanguageServer;

namespace Piper.Tests;

public sealed class LanguageServerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("piper-lsp-").FullName;
    private readonly List<JsonObject> _notifications = [];
    private readonly PiperLanguageServer _server;

    public LanguageServerTests()
    {
        _server = new PiperLanguageServer(message =>
        {
            _notifications.Add(message);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string Program = """
        record Point(x: double, y: double);

        extend Point {
            fn length() => double = Math.Sqrt(this.x * this.x + this.y * this.y);
        }

        fn scale(p: Point, by: double) => Point = Point(p.x * by, p.y * by);

        fn Main() {
            let p = Point(3.0, 4.0);
            let q = scale(p, 2.0);
            Print(q.length());
        }
        """;

    private async Task<string> OpenAsync(string text, string name = "main.piper")
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, text);
        var uri = PiperLanguageServer.UriOf(path);
        await _server.HandleAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "piper", ["version"] = 1, ["text"] = text },
        });
        return uri;
    }

    private static JsonObject At(string uri, string text, string marker, int offset = 0)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal) + offset;
        var before = text[..index];
        var line = before.Count(c => c == '\n');
        var character = index - (before.LastIndexOf('\n') + 1);
        return new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
        };
    }

    [Fact]
    public async Task Publishes_diagnostics_as_documents_change()
    {
        var uri = await OpenAsync("fn Main() { let x: int = \"text\"; }");
        var published = Assert.Single(_notifications);
        Assert.Equal("textDocument/publishDiagnostics", published["method"]!.GetValue<string>());
        var diagnostic = Assert.Single(published["params"]!["diagnostics"]!.AsArray());
        Assert.StartsWith("PIP", diagnostic!["code"]!.GetValue<string>());
        Assert.Equal(1, diagnostic["severity"]!.GetValue<int>());

        await _server.HandleAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "fn Main() { let x: int = 1; }" }),
        });
        Assert.Empty(_notifications[^1]["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task Completion_on_an_optional_offers_its_value_s_members_and_ways_to_unwrap_it()
    {
        var text = "record User(name: string);\nfn F(u: User?, n: int?) {\n    Print(u.);\n    Print(n.);\n}";
        var uri = await OpenAsync(text);

        var items = ((JsonArray)(await _server.HandleAsync("textDocument/completion", At(uri, text, "u.)", 2)))!).OfType<JsonObject>().ToList();
        var name = items.Single(i => i["label"]!.GetValue<string>() == "name");
        Assert.Equal("?.", name["additionalTextEdits"]![0]!["newText"]!.GetValue<string>());
        // The edit replaces the `.` itself: column 11 of `    Print(u.);`.
        var dot = name["additionalTextEdits"]![0]!["range"]!;
        Assert.Equal((2, 11, 12), (dot["start"]!["line"]!.GetValue<int>(), dot["start"]!["character"]!.GetValue<int>(), dot["end"]!["character"]!.GetValue<int>()));

        var match = items.Single(i => i["label"]!.GetValue<string>() == "match");
        Assert.StartsWith("match u {\n\tSome(${1:value}) => $2,", match["textEdit"]!["newText"]!.GetValue<string>());
        Assert.Equal("u.match", match["filterText"]!.GetValue<string>());
        Assert.Contains(items, i => i["label"]!.GetValue<string>() == "letElse");

        // An int?'s value is an int, so int's .NET members are offered too.
        var numbers = ((JsonArray)(await _server.HandleAsync("textDocument/completion", At(uri, text, "n.)", 2)))!).OfType<JsonObject>().ToList();
        Assert.Contains(numbers, i => i["label"]!.GetValue<string>() == "CompareTo" && i["additionalTextEdits"] is not null);
    }

    [Fact]
    public async Task Doc_comments_and_defaults_show_in_hover_completion_and_signature_help()
    {
        var text = "/// Connects to a server.\nfn Connect(\n    /// The host name.\n    host: string,\n    port: int = 443,\n) => int = port;\nfn Main() { Print(Connect(\"x\")); }";
        var uri = await OpenAsync(text);

        var hover = await _server.HandleAsync("textDocument/hover", At(uri, text, "Connect(\"x\")"));
        var value = hover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("fn Connect(host: string, port: int = 443) => int", value);
        Assert.EndsWith("\n\nConnects to a server.", value);

        var help = await _server.HandleAsync("textDocument/signatureHelp", At(uri, text, "\"x\")"));
        var signature = help!["signatures"]![0]!;
        Assert.Equal("Connects to a server.", signature["documentation"]!.GetValue<string>());
        Assert.Equal("The host name.", signature["parameters"]![0]!["documentation"]!.GetValue<string>());

        var completion = (JsonArray)(await _server.HandleAsync("textDocument/completion", At(uri, text, "Connect(\"x\")")))!;
        var item = completion.First(i => i!["label"]!.GetValue<string>() == "Connect")!;
        Assert.Equal("Connects to a server.", item["documentation"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Hover_and_signature_help_show_preconditions()
    {
        var text = "fn Sqrt(x: double) => double requires x >= 0.0 = x;\nrecord Pct(v: int) requires v <= 100;\nfn Main() { Print(Sqrt(4.0)); Print(Pct(1)); }";
        var uri = await OpenAsync(text);

        var hover = await _server.HandleAsync("textDocument/hover", At(uri, text, "Sqrt(4.0)"));
        Assert.Contains("fn Sqrt(x: double) => double\n    requires x >= 0.0", hover!["contents"]!["value"]!.GetValue<string>());
        hover = await _server.HandleAsync("textDocument/hover", At(uri, text, "Pct(1)"));
        Assert.Contains("record Pct(v: int)\n    requires v <= 100", hover!["contents"]!["value"]!.GetValue<string>());

        var help = await _server.HandleAsync("textDocument/signatureHelp", At(uri, text, "4.0)"));
        Assert.Equal("requires x >= 0.0", help!["signatures"]![0]!["documentation"]!.GetValue<string>());
    }

    [Fact]
    public async Task Hover_describes_symbols_and_expressions()
    {
        var uri = await OpenAsync(Program);
        var hover = await _server.HandleAsync("textDocument/hover", At(uri, Program, "q = scale"));
        Assert.Contains("let q: Point", hover!["contents"]!["value"]!.GetValue<string>());

        hover = await _server.HandleAsync("textDocument/hover", At(uri, Program, "scale(p, 2.0)"));
        Assert.Contains("fn scale(p: Point, by: double) => Point", hover!["contents"]!["value"]!.GetValue<string>());

        hover = await _server.HandleAsync("textDocument/hover", At(uri, Program, "length())"));
        Assert.Contains("fn Point.length() => double", hover!["contents"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Goes_to_definitions_and_finds_references()
    {
        var uri = await OpenAsync(Program);
        var definition = await _server.HandleAsync("textDocument/definition", At(uri, Program, "scale(p, 2.0)"));
        Assert.Equal(uri, definition!["uri"]!.GetValue<string>());
        Assert.Equal(6, definition["range"]!["start"]!["line"]!.GetValue<int>());

        var references = await _server.HandleAsync("textDocument/references", At(uri, Program, "p = Point"));
        Assert.Equal(2, references!.AsArray().Count);   // `let p` and `scale(p, …)`
    }

    [Fact]
    public async Task Renames_every_occurrence()
    {
        var uri = await OpenAsync(Program);
        var parameters = At(uri, Program, "Point(3.0");
        parameters["newName"] = "Vector";
        var edit = await _server.HandleAsync("textDocument/rename", parameters);
        var edits = edit!["changes"]![uri]!.AsArray();
        Assert.Equal(Program.Split("Point").Length - 1, edits.Count);
    }

    [Fact]
    public async Task Completes_members_after_a_dot_and_names_elsewhere()
    {
        var text = Program.Replace("Print(q.length());", "Print(q.);");
        var uri = await OpenAsync(text);
        var items = await _server.HandleAsync("textDocument/completion", At(uri, text, "q.)", 2));
        var labels = items!.AsArray().Select(i => i!["label"]!.GetValue<string>()).ToList();
        Assert.Contains("x", labels);
        Assert.Contains("length", labels);

        items = await _server.HandleAsync("textDocument/completion", At(uri, text, "Print(q.)", 6));
        labels = [.. items!.AsArray().Select(i => i!["label"]!.GetValue<string>())];
        Assert.Contains("p", labels);
        Assert.Contains("scale", labels);
        Assert.Contains("Point", labels);
        Assert.Contains("match", labels);
    }

    [Fact]
    public async Task Lists_document_symbols()
    {
        var uri = await OpenAsync(Program);
        var symbols = await _server.HandleAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
        var names = symbols!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Point", "extend Point", "scale", "Main"], names);
    }

    [Fact]
    public async Task A_folder_with_piper_json_is_one_program()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "piper.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_directory, "shapes.piper"), "record Square(side: double);");
        var text = "fn Main() { let s = Square(2.0); Print(s.side); }";
        await OpenAsync(text);
        Assert.All(_notifications, n => Assert.Empty(n["params"]!["diagnostics"]!.AsArray()));
    }

    [Fact]
    public async Task Publishes_piper_json_errors_and_clears_them_when_fixed()
    {
        var project = Path.Combine(_directory, "piper.json");
        await File.WriteAllTextAsync(project, "{ \"output\": \"dll\" }");
        await OpenAsync("fn Main() { }");

        var published = _notifications.Last(n => n["params"]!["uri"]!.GetValue<string>() == PiperLanguageServer.UriOf(project));
        var error = Assert.Single(published["params"]!["diagnostics"]!.AsArray())!;
        Assert.Equal("PIP0400", error["code"]!.GetValue<string>());
        Assert.Equal(2, error["range"]!["start"]!["character"]!.GetValue<int>());

        await File.WriteAllTextAsync(project, "{ \"output\": \"exe\" }");
        await _server.HandleAsync("workspace/didChangeWatchedFiles", new JsonObject
        {
            ["changes"] = new JsonArray(new JsonObject { ["uri"] = PiperLanguageServer.UriOf(project), ["type"] = 2 }),
        });
        Assert.Empty(_notifications[^1]["params"]!["diagnostics"]!.AsArray());
        Assert.Equal(PiperLanguageServer.UriOf(project), _notifications[^1]["params"]!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task Serves_framed_json_rpc_over_streams()
    {
        static byte[] Frame(string json) => Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");
        var input = new MemoryStream([
            .. Frame("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""),
            .. Frame("""{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""),
            .. Frame("""{"jsonrpc":"2.0","method":"exit"}"""),
        ]);
        var output = new MemoryStream();
        Assert.Equal(0, await PiperLanguageServer.RunAsync(input, output));

        output.Position = 0;
        var connection = new JsonRpcConnection(output, Stream.Null);
        var initialized = await connection.ReadAsync();
        Assert.Equal(1, initialized!["id"]!.GetValue<int>());
        Assert.True(initialized["result"]!["capabilities"]!["hoverProvider"]!.GetValue<bool>());
        var shutdown = await connection.ReadAsync();
        Assert.Equal(2, shutdown!["id"]!.GetValue<int>());
    }
    private static JsonObject WholeDocument(string uri, string text) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = uri },
        ["range"] = new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
            ["end"] = new JsonObject { ["line"] = text.Count(c => c == '\n') + 1, ["character"] = 0 },
        },
    };

    [Fact]
    public async Task Signature_help_shows_the_parameter_being_written()
    {
        var text = Program.Replace("let q = scale(p, 2.0);", "let q = scale(p, ");
        var uri = await OpenAsync(text);

        var help = await _server.HandleAsync("textDocument/signatureHelp", At(uri, text, "scale(p, ", "scale(p, ".Length));

        var signature = Assert.Single(help!["signatures"]!.AsArray());
        Assert.Equal("fn scale(p: Point, by: double) => Point", signature!["label"]!.GetValue<string>());
        Assert.Equal(1, help["activeParameter"]!.GetValue<int>());
    }

    [Fact]
    public async Task Signature_help_lists_records_and_dotnet_overloads()
    {
        var text = "fn Main() {\n    let p = Point(1.0, 2.0);\n    System.Console.WriteLine(\"{p.x}\");\n}\nrecord Point(x: double, y: double);";
        var uri = await OpenAsync(text);

        var record = await _server.HandleAsync("textDocument/signatureHelp", At(uri, text, "1.0, 2.0", "1.0, ".Length));
        Assert.Equal("Point(x: double, y: double)", record!["signatures"]![0]!["label"]!.GetValue<string>());
        Assert.Equal(1, record["activeParameter"]!.GetValue<int>());

        var overloads = await _server.HandleAsync("textDocument/signatureHelp", At(uri, text, "\"{p.x}\""));
        var labels = overloads!["signatures"]!.AsArray().Select(s => s!["label"]!.GetValue<string>()).ToList();
        Assert.Contains("fn Console.WriteLine(value: string)", labels);
        Assert.Contains("fn Console.WriteLine()", labels);
        // The comma inside the interpolated string isn't an argument separator.
        Assert.Equal(0, overloads["activeParameter"]!.GetValue<int>());
    }

    [Fact]
    public async Task Inlay_hints_show_inferred_types()
    {
        var uri = await OpenAsync(Program);

        var hints = (JsonArray)(await _server.HandleAsync("textDocument/inlayHint", WholeDocument(uri, Program)))!;

        var labels = hints.Select(h => h!["label"]!.GetValue<string>()).ToList();
        Assert.Equal([": Point", ": Point"], labels);
        var position = hints[0]!["position"]!;
        Assert.Equal((9, 9), (position["line"]!.GetValue<int>(), position["character"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Semantic_tokens_classify_names()
    {
        var text = "enum Shape { Circle(r: double), Empty }\nfn area(s: Shape) => double = match s { Circle(r) => r * r, Empty => 0.0 };";
        var uri = await OpenAsync(text);

        var tokens = await _server.HandleAsync("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });

        // Decode the relative groups of five into (line, column, length, type).
        var data = tokens!["data"]!.AsArray().Select(d => d!.GetValue<int>()).ToList();
        var decoded = new List<(int Line, int Column, string Text, string Type)>();
        int line = 0, column = 0;
        var lines = text.Split('\n');
        for (int i = 0; i < data.Count; i += 5)
        {
            column = data[i] == 0 ? column + data[i + 1] : data[i + 1];
            line += data[i];
            decoded.Add((line, column, lines[line].Substring(column, data[i + 2]), SemanticTokens.TokenTypes[data[i + 3]]));
        }
        Assert.Contains((0, 5, "Shape", "enum"), decoded);
        Assert.Contains((0, 13, "Circle", "enumMember"), decoded);
        Assert.Contains((1, 3, "area", "function"), decoded);
        Assert.Contains((1, 8, "s", "parameter"), decoded);
        Assert.Contains(decoded, t => t is { Line: 1, Text: "Empty", Type: "enumMember" });
    }

    [Fact]
    public async Task A_quick_fix_adds_missing_match_arms()
    {
        var text = """
            enum Shape { Circle(r: double), Rect(w: double, h: double), Empty }

            fn area(s: Shape) => double = match s {
                Circle(r) => r * r
            };
            """.ReplaceLineEndings("\n");
        var uri = await OpenAsync(text);
        var diagnostics = _notifications[^1]["params"]!["diagnostics"]!.AsArray();
        Assert.Equal("PIP0230", Assert.Single(diagnostics)!["code"]!.GetValue<string>());

        var request = WholeDocument(uri, text);
        request["context"] = new JsonObject { ["diagnostics"] = diagnostics.DeepClone() };
        var actions = (JsonArray)(await _server.HandleAsync("textDocument/codeAction", request))!;

        var action = Assert.Single(actions)!;
        Assert.Equal("Add the missing match arms", action["title"]!.GetValue<string>());
        var edit = Assert.Single(action["edit"]!["changes"]![uri]!.AsArray())!;
        var source = new Piper.Text.SourceText(text);
        int Offset(JsonNode position) => source.GetPosition(position["line"]!.GetValue<int>(), position["character"]!.GetValue<int>());
        var start = Offset(edit["range"]!["start"]!);
        var fixedText = text[..start] + edit["newText"]!.GetValue<string>() + text[Offset(edit["range"]!["end"]!)..];
        Assert.Equal("""
            enum Shape { Circle(r: double), Rect(w: double, h: double), Empty }

            fn area(s: Shape) => double = match s {
                Circle(r) => r * r,
                Rect(_, _) => throw System.NotImplementedException(),
                Empty => throw System.NotImplementedException(),
            };
            """.ReplaceLineEndings("\n"), fixedText);

        await _server.HandleAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = fixedText }),
        });
        Assert.Empty(_notifications[^1]["params"]!["diagnostics"]!.AsArray());
    }
}
