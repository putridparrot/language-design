using System.Collections.Immutable;
using System.Text.Json;
using Piper.Diagnostics;
using Piper.Text;

namespace Piper;

public enum PiperOutput
{
    Exe,
    Library,
}

/// <summary>
/// A program folder's <c>piper.json</c>: <c>{ "name": "app", "output": "exe" }</c>. Both keys are optional and an empty
/// file is allowed. Any other key is an error, so later versions can add keys without changing what existing files mean.
/// Comments and trailing commas are allowed, as in other .NET JSON configuration files.
/// </summary>
/// <param name="Name">The assembly's name (default: the folder's).</param>
/// <param name="Output">What <c>piper build</c> makes of the folder: an executable or a library (default: C# source).</param>
public sealed record PiperProject(string? Name, PiperOutput? Output)
{
    public const string FileName = "piper.json";

    public static PiperProject Empty { get; } = new(null, null);

    /// <summary>Reads a piper.json; on failure, the project is null and there are errors (PIP0400) explaining why.</summary>
    public static (PiperProject? Project, ImmutableArray<Diagnostic> Errors) Load(string path) => Parse(new SourceText(File.ReadAllText(path), path));

    public static (PiperProject? Project, ImmutableArray<Diagnostic> Errors) Parse(SourceText source)
    {
        if (string.IsNullOrWhiteSpace(source.Text))
            return (Empty, []);

        var errors = ImmutableArray.CreateBuilder<Diagnostic>();
        void Error(TextSpan span, string message) =>
            errors.Add(new Diagnostic("PIP0400", DiagnosticSeverity.Error, span, message) { Source = source });

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source.Text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            var at = exception.LineNumber is { } line && exception.BytePositionInLine is { } column && line < source.LineCount
                ? source.GetPosition((int)line, (int)column)
                : 0;
            Error(new TextSpan(Math.Min(at, source.Length), 0), $"This isn't valid JSON: {exception.Message}");
            return (null, errors.ToImmutable());
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Error(new TextSpan(0, 0), "piper.json must hold a JSON object, such as { \"name\": \"app\", \"output\": \"exe\" }.");
                return (null, errors.ToImmutable());
            }

            string? name = null;
            PiperOutput? output = null;
            int searchFrom = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // JsonDocument doesn't keep positions, but keys appear in the text in order.
                var quoted = JsonSerializer.Serialize(property.Name);
                int at = source.Text.IndexOf(quoted, searchFrom, StringComparison.Ordinal);
                var key = at < 0 ? new TextSpan(0, 0) : new TextSpan(at, quoted.Length);
                if (at >= 0)
                    searchFrom = at + quoted.Length;

                switch (property.Name)
                {
                    case "name" when property.Value.ValueKind == JsonValueKind.String &&
                                     property.Value.GetString() is { Length: > 0 } value &&
                                     value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0:
                        name = value;
                        break;
                    case "name":
                        Error(key, "\"name\" must be a non-empty string that can be a file name, such as \"app\".");
                        break;
                    case "output" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "exe" or "library":
                        output = property.Value.GetString() == "exe" ? PiperOutput.Exe : PiperOutput.Library;
                        break;
                    case "output":
                        Error(key, "\"output\" must be \"exe\" or \"library\".");
                        break;
                    default:
                        Error(key, $"Unknown key \"{property.Name}\"; piper.json can have \"name\" and \"output\".");
                        break;
                }
            }
            return errors.Count == 0 ? (new PiperProject(name, output), []) : (null, errors.ToImmutable());
        }
    }
}
