using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>
/// A Language Server Protocol server for Piper: diagnostics as you type, hover, go to definition, find references,
/// rename, completion, signature help, inlay hints, semantic highlighting, quick fixes and an outline. Every edit re-analyzes the document's program (see <see cref="Workspace"/>) with the
/// compiler's parser and binder, which recover from errors, so incomplete code still gets answers.
/// </summary>
public sealed class PiperLanguageServer
{
    private readonly Workspace _workspace = new();
    private readonly Func<JsonObject, Task> _send;
    private readonly HashSet<string> _published = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _shutdown;

    /// <param name="send">Writes a message (a notification such as published diagnostics) to the client.</param>
    public PiperLanguageServer(Func<JsonObject, Task> send) => _send = send;

    /// <summary>Serves LSP over the given streams (stdin and stdout for `piper lsp`) until the client exits.</summary>
    public static async Task<int> RunAsync(Stream input, Stream output)
    {
        var connection = new JsonRpcConnection(input, output);
        var server = new PiperLanguageServer(message => connection.WriteAsync(message));
        while (await connection.ReadAsync() is { } message)
        {
            var method = message["method"]?.GetValue<string>();
            if (method == "exit")
                return server._shutdown ? 0 : 1;

            var id = message["id"]?.DeepClone();
            JsonObject? response = null;
            try
            {
                var result = await server.HandleAsync(method, message["params"] as JsonObject);
                if (id is not null)
                    response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (Exception exception) when (id is not null)
            {
                response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = exception is MethodNotFoundException ? -32601 : -32603, ["message"] = exception.Message },
                };
            }
            catch (Exception)
            {
                // A failing notification has no one to answer; keep serving.
            }
            if (response is not null)
                await connection.WriteAsync(response);
        }
        return 0;
    }

    private sealed class MethodNotFoundException(string method) : Exception($"Unknown method '{method}'.");

    /// <summary>Handles one request or notification, returning the result for a request.</summary>
    public async Task<JsonNode?> HandleAsync(string? method, JsonObject? parameters)
    {
        switch (method)
        {
            case "initialize":
                return Initialize();
            case "initialized":
            case "$/cancelRequest":
            case "$/setTrace":
            case "workspace/didChangeConfiguration":
                return null;
            case "shutdown":
                _shutdown = true;
                return null;
            case "textDocument/didOpen":
            {
                var document = parameters!["textDocument"]!;
                var path = PathOf(document["uri"]!.GetValue<string>());
                _workspace.Open(path, document["text"]!.GetValue<string>());
                await PublishAsync(path);
                return null;
            }
            case "textDocument/didChange":
            {
                // Full sync: the last change holds the whole text.
                var path = PathOf(parameters!["textDocument"]!["uri"]!.GetValue<string>());
                var changes = parameters["contentChanges"]!.AsArray();
                _workspace.Change(path, changes[^1]!["text"]!.GetValue<string>());
                await PublishAsync(path);
                return null;
            }
            case "textDocument/didSave":
                await PublishAsync(PathOf(parameters!["textDocument"]!["uri"]!.GetValue<string>()));
                return null;
            case "textDocument/didClose":
            {
                var path = PathOf(parameters!["textDocument"]!["uri"]!.GetValue<string>());
                _workspace.Close(path);
                // Its unsaved changes are gone, so the program on disk is what counts now.
                if (File.Exists(path))
                    await PublishAsync(path);
                else
                    await SendDiagnosticsAsync(path, []);
                return null;
            }
            case "workspace/didChangeWatchedFiles":
            {
                // piper.json isn't a document the client opens, so its changes arrive as file events.
                foreach (var change in parameters!["changes"]!.AsArray().OfType<JsonObject>())
                {
                    var path = PathOf(change["uri"]!.GetValue<string>());
                    if (!string.Equals(Path.GetFileName(path), PiperProject.FileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (change["type"]?.GetValue<int>() == 3)
                        await SendDiagnosticsAsync(path, []);
                    else
                        await PublishProjectAsync(Path.GetDirectoryName(path));
                }
                return null;
            }
            case "textDocument/hover":
                return At(parameters!, Hover);
            case "textDocument/definition":
                return At(parameters!, Definition);
            case "textDocument/references":
                return At(parameters!, (analysis, source, position) =>
                    References(analysis, source, position, parameters!["context"]?["includeDeclaration"]?.GetValue<bool>() ?? true));
            case "textDocument/documentHighlight":
                return At(parameters!, Highlights);
            case "textDocument/prepareRename":
                return At(parameters!, PrepareRename);
            case "textDocument/rename":
                return At(parameters!, (analysis, source, position) => Rename(analysis, source, position, parameters!["newName"]!.GetValue<string>()));
            case "textDocument/completion":
                return At(parameters!, (analysis, source, position) => Completion.Items(analysis, source, position));
            case "textDocument/signatureHelp":
                return At(parameters!, SignatureHelp.Help);
            case "textDocument/inlayHint":
                return InDocument(parameters!, (analysis, source) => InlayHints.Hints(analysis, source, SpanOf(source, parameters!["range"]!)));
            case "textDocument/semanticTokens/full":
                return InDocument(parameters!, SemanticTokens.Tokens);
            case "textDocument/codeAction":
                return InDocument(parameters!, (analysis, source) =>
                    CodeActions.Actions(analysis, source, SpanOf(source, parameters!["range"]!), parameters!["context"]?["diagnostics"] as JsonArray ?? []));
            case "textDocument/documentSymbol":
            {
                var path = PathOf(parameters!["textDocument"]!["uri"]!.GetValue<string>());
                var analysis = _workspace.AnalysisOf(path);
                var unit = analysis.Units.FirstOrDefault(u => Workspace.SourceOf(analysis, path) == u.Source);
                return unit is null ? new JsonArray() : DocumentSymbols(unit);
            }
            default:
                if (method is not null && method.StartsWith("$/", StringComparison.Ordinal))
                    return null;
                throw new MethodNotFoundException(method ?? "?");
        }
    }

    private static JsonObject Initialize() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1, ["save"] = true },
            ["hoverProvider"] = true,
            ["definitionProvider"] = true,
            ["referencesProvider"] = true,
            ["documentHighlightProvider"] = true,
            ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
            ["documentSymbolProvider"] = true,
            ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".") },
            ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ","), ["retriggerCharacters"] = new JsonArray(")") },
            ["inlayHintProvider"] = true,
            ["semanticTokensProvider"] = new JsonObject { ["legend"] = SemanticTokens.Legend(), ["full"] = true },
            ["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix") },
        },
        ["serverInfo"] = new JsonObject { ["name"] = "piper", ["version"] = typeof(Compilation).Assembly.GetName().Version?.ToString() },
    };

    // Diagnostics

    /// <summary>Re-analyzes the document's program and publishes diagnostics for each of its files.</summary>
    private async Task PublishAsync(string path)
    {
        var analysis = _workspace.Analyze(path);
        foreach (var source in analysis.Sources)
        {
            // Other files of the program get diagnostics too, but only open ones keep them published.
            var diagnostics = analysis.Diagnostics.Where(d => d.Source == source).ToList();
            if (_workspace.IsOpen(source.FileName) || diagnostics.Count > 0 || _published.Contains(source.FileName))
                await SendDiagnosticsAsync(source.FileName, diagnostics);
        }
        await PublishProjectAsync(Workspace.ProgramRoot(path));
    }

    /// <summary>Publishes the errors in the piper.json of the program in <paramref name="root"/>, if it has one.</summary>
    private async Task PublishProjectAsync(string? root)
    {
        if (root is null)
            return;
        var path = Path.Combine(root, PiperProject.FileName);
        ImmutableArray<Diagnostic> errors;
        try
        {
            if (!File.Exists(path))
                return;
            errors = PiperProject.Load(path).Errors;
        }
        catch (IOException)
        {
            return;
        }
        if (errors.Length > 0 || _published.Contains(path))
            await SendDiagnosticsAsync(path, errors);
    }

    private async Task SendDiagnosticsAsync(string path, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count > 0)
            _published.Add(path);
        else
            _published.Remove(path);

        var source = diagnostics.FirstOrDefault()?.Source;
        var items = new JsonArray();
        foreach (var diagnostic in diagnostics)
        {
            items.Add(new JsonObject
            {
                ["range"] = Range(source!, diagnostic.Span),
                ["severity"] = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
                ["code"] = diagnostic.Code,
                ["source"] = "piper",
                ["message"] = diagnostic.Message,
            });
        }
        await _send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = new JsonObject { ["uri"] = UriOf(path), ["diagnostics"] = items },
        });
    }

    // Requests at a position

    private JsonNode? At(JsonObject parameters, Func<AnalysisResult, SourceText, int, JsonNode?> handler)
    {
        var path = PathOf(parameters["textDocument"]!["uri"]!.GetValue<string>());
        var analysis = _workspace.AnalysisOf(path);
        if (Workspace.SourceOf(analysis, path) is not { } source)
            return null;
        var position = parameters["position"]!;
        var offset = source.GetPosition(position["line"]!.GetValue<int>(), position["character"]!.GetValue<int>());
        return handler(analysis, source, offset);
    }

    private JsonNode? InDocument(JsonObject parameters, Func<AnalysisResult, SourceText, JsonNode?> handler)
    {
        var path = PathOf(parameters["textDocument"]!["uri"]!.GetValue<string>());
        var analysis = _workspace.AnalysisOf(path);
        return Workspace.SourceOf(analysis, path) is { } source ? handler(analysis, source) : null;
    }

    private static TextSpan SpanOf(SourceText source, JsonNode range)
    {
        int Offset(JsonNode position) => source.GetPosition(position["line"]!.GetValue<int>(), position["character"]!.GetValue<int>());
        return TextSpan.FromBounds(Offset(range["start"]!), Offset(range["end"]!));
    }

    private static JsonNode? Hover(AnalysisResult analysis, SourceText source, int position)
    {
        string text;
        TextSpan span;
        if (analysis.Semantic.OccurrenceAt(source, position) is { } occurrence)
        {
            text = SymbolDisplay.Describe(occurrence.Symbol);
            span = occurrence.Span;
        }
        else if (analysis.Semantic.ExpressionAt(source, position) is { Type: var type } expression && type != TypeSymbol.Error)
        {
            text = type.ToString();
            span = expression.Span;
        }
        else
        {
            return null;
        }
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = $"```piper\n{text}\n```" },
            ["range"] = Range(source, span),
        };
    }

    private static JsonNode? Definition(AnalysisResult analysis, SourceText source, int position) =>
        analysis.Semantic.OccurrenceAt(source, position) is { } occurrence &&
        analysis.Semantic.DeclarationOf(occurrence.Symbol) is { } declaration
            ? Location(declaration.File, declaration.Span)
            : null;

    private static JsonNode? References(AnalysisResult analysis, SourceText source, int position, bool includeDeclaration)
    {
        if (analysis.Semantic.OccurrenceAt(source, position) is not { } occurrence)
            return null;
        var locations = new JsonArray();
        foreach (var reference in analysis.Semantic.ReferencesTo(occurrence.Symbol).Where(r => includeDeclaration || !r.IsDeclaration))
            locations.Add(Location(reference.File, reference.Span));
        return locations;
    }

    private static JsonNode? Highlights(AnalysisResult analysis, SourceText source, int position)
    {
        if (analysis.Semantic.OccurrenceAt(source, position) is not { } occurrence)
            return null;
        var highlights = new JsonArray();
        foreach (var reference in analysis.Semantic.ReferencesTo(occurrence.Symbol).Where(r => r.File == source))
            highlights.Add(new JsonObject { ["range"] = Range(source, reference.Span), ["kind"] = reference.IsDeclaration ? 3 : 2 });
        return highlights;
    }

    /// <summary>Only symbols declared in the program can be renamed, not builtins or .NET members.</summary>
    private static JsonNode? PrepareRename(AnalysisResult analysis, SourceText source, int position) =>
        analysis.Semantic.OccurrenceAt(source, position) is { } occurrence && analysis.Semantic.DeclarationOf(occurrence.Symbol) is not null
            ? Range(source, occurrence.Span)
            : null;

    private static JsonNode? Rename(AnalysisResult analysis, SourceText source, int position, string newName)
    {
        if (analysis.Semantic.OccurrenceAt(source, position) is not { } occurrence || analysis.Semantic.DeclarationOf(occurrence.Symbol) is null)
            return null;
        var changes = new JsonObject();
        foreach (var file in analysis.Semantic.ReferencesTo(occurrence.Symbol).GroupBy(r => r.File))
        {
            var edits = new JsonArray();
            foreach (var reference in file)
                edits.Add(new JsonObject { ["range"] = Range(file.Key, reference.Span), ["newText"] = newName });
            changes[UriOf(file.Key.FileName)] = edits;
        }
        return new JsonObject { ["changes"] = changes };
    }

    // Outline

    private static JsonArray DocumentSymbols(CompilationUnitSyntax unit)
    {
        var symbols = new JsonArray();
        foreach (var declaration in unit.Declarations)
        {
            if (declaration.Name.IsMissing)
                continue;
            var (kind, children) = declaration switch
            {
                FunctionDeclarationSyntax => (12, []),
                RecordDeclarationSyntax r => (23, r.Fields.Select(f => (f.Name, f.Span, 8))),
                EnumDeclarationSyntax e => (10, e.Variants.Select(v => (v.Name, v.Span, 22))),
                TraitDeclarationSyntax t => (11, t.Methods.Select(m => (m.Name, m.Span, 6))),
                ExtendDeclarationSyntax x => (3, x.Methods.Select(m => (m.Name, m.Span, 6))),
                _ => (13, Enumerable.Empty<(Token, TextSpan, int)>()),
            };
            var name = declaration is ExtendDeclarationSyntax ? $"extend {declaration.Name.Text}" : declaration.Name.Text;
            var symbol = DocumentSymbol(unit.Source, name, kind, declaration.Span, declaration.Name.Span);
            var childArray = new JsonArray();
            foreach (var (childName, childSpan, childKind) in children.Where(c => !c.Item1.IsMissing))
                childArray.Add(DocumentSymbol(unit.Source, childName.Text, childKind, childSpan, childName.Span));
            symbol["children"] = childArray;
            symbols.Add(symbol);
        }
        return symbols;
    }

    private static JsonObject DocumentSymbol(SourceText source, string name, int kind, TextSpan span, TextSpan selection) => new()
    {
        ["name"] = name,
        ["kind"] = kind,
        ["range"] = Range(source, span),
        ["selectionRange"] = Range(source, selection),
    };

    // Positions and paths

    public static JsonObject Range(SourceText source, TextSpan span) => new()
    {
        ["start"] = Position(source, span.Start),
        ["end"] = Position(source, span.End),
    };

    private static JsonObject Position(SourceText source, int offset)
    {
        var (line, column) = source.GetLineColumn(Math.Min(offset, source.Length));
        return new JsonObject { ["line"] = line - 1, ["character"] = column - 1 };
    }

    private static JsonObject Location(SourceText source, TextSpan span) => new()
    {
        ["uri"] = UriOf(source.FileName),
        ["range"] = Range(source, span),
    };

    public static string PathOf(string uri) => Workspace.Normalize(new Uri(uri).LocalPath);

    public static string UriOf(string path) => new Uri(path).AbsoluteUri;
}
