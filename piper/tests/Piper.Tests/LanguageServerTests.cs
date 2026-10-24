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

        fn main() {
            let p = Point(3.0, 4.0);
            let q = scale(p, 2.0);
            print(q.length());
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
        var uri = await OpenAsync("fn main() { let x: int = \"text\"; }");
        var published = Assert.Single(_notifications);
        Assert.Equal("textDocument/publishDiagnostics", published["method"]!.GetValue<string>());
        var diagnostic = Assert.Single(published["params"]!["diagnostics"]!.AsArray());
        Assert.StartsWith("PIP", diagnostic!["code"]!.GetValue<string>());
        Assert.Equal(1, diagnostic["severity"]!.GetValue<int>());

        await _server.HandleAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "fn main() { let x: int = 1; }" }),
        });
        Assert.Empty(_notifications[^1]["params"]!["diagnostics"]!.AsArray());
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
        var text = Program.Replace("print(q.length());", "print(q.);");
        var uri = await OpenAsync(text);
        var items = await _server.HandleAsync("textDocument/completion", At(uri, text, "q.)", 2));
        var labels = items!.AsArray().Select(i => i!["label"]!.GetValue<string>()).ToList();
        Assert.Contains("x", labels);
        Assert.Contains("length", labels);

        items = await _server.HandleAsync("textDocument/completion", At(uri, text, "print(q.)", 6));
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
        Assert.Equal(["Point", "extend Point", "scale", "main"], names);
    }

    [Fact]
    public async Task A_folder_with_piper_json_is_one_program()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "piper.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_directory, "shapes.piper"), "record Square(side: double);");
        var text = "fn main() { let s = Square(2.0); print(s.side); }";
        await OpenAsync(text);
        Assert.All(_notifications, n => Assert.Empty(n["params"]!["diagnostics"]!.AsArray()));
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
}
