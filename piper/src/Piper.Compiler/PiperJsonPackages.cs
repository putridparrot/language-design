using System.Text;
using System.Text.Json;

namespace Piper;

/// <summary>
/// Adding a NuGet package to a piper.json, and removing one (<c>piper package add</c>, <c>piper package remove</c>). The
/// file is edited as text, changing only the package's entry, so its comments and layout stay as they were.
/// </summary>
public static class PiperJsonPackages
{
    private static readonly JsonReaderOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>An entry of "packages": where its name starts and its value ends, in bytes.</summary>
    private sealed record Entry(string Id, int NameStart, int ValueStart, int ValueEnd);

    /// <summary>Where piper.json's parts are, in bytes: the root's end, its last value's, and "packages" with its entries.</summary>
    private sealed record Layout(int RootStart, int RootEnd, int? LastRootValueEnd, int? LastRootNameStart,
        int? PackagesStart, int? PackagesEnd, List<Entry> Entries);

    /// <summary>
    /// piper.json's <paramref name="text"/> with <paramref name="id"/> at <paramref name="version"/> in its "packages":
    /// its version changed if it's there (matched as NuGet matches ids, without regard to case), added if not, and
    /// "packages" added if the file has none. An empty text is a new file.
    /// </summary>
    public static string Add(string text, string id, string version)
    {
        if (text.Trim().Length == 0)
            return $"{{\n  \"packages\": {{ {Quote(id)}: {Quote(version)} }}\n}}\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        var layout = Read(bytes);
        var entry = $"{Quote(id)}: {Quote(version)}";

        // Already there: only its version changes, its name staying as written.
        if (layout.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) is { } existing)
            return Splice(bytes, existing.ValueStart, existing.ValueEnd, Quote(version));
        if (layout is { PackagesStart: { } start, PackagesEnd: { } end })
        {
            if (layout.Entries.Count == 0)
                return Splice(bytes, start, end, $"{{ {entry} }}");
            var last = layout.Entries[^1];
            // On lines of their own, the new one goes on a line of its own too, indented as the last.
            var multiline = Array.IndexOf(bytes, (byte)'\n', start, end - start) >= 0;
            return multiline ? NewLineAfter(bytes, last.ValueEnd, last.NameStart, entry) : Splice(bytes, last.ValueEnd, last.ValueEnd, $", {entry}");
        }
        var packages = $"\"packages\": {{ {entry} }}";
        if (layout is { LastRootValueEnd: { } after, LastRootNameStart: { } name })
            return NewLineAfter(bytes, after, name, packages);
        return Splice(bytes, layout.RootStart, layout.RootEnd, $"{{\n  {packages}\n}}");
    }

    /// <summary>
    /// <paramref name="content"/> on a new line after the value ending at <paramref name="valueEnd"/>, indented as the
    /// line <paramref name="lineOf"/> is on: the comma right after the value, and the new line after any comment that
    /// ends the value's line, which stays with the value it's about.
    /// </summary>
    private static string NewLineAfter(byte[] bytes, int valueEnd, int lineOf, string content)
    {
        var lineEnd = Array.IndexOf(bytes, (byte)'\n', valueEnd);
        var rest = Encoding.UTF8.GetString(bytes, valueEnd, (lineEnd < 0 ? bytes.Length : lineEnd) - valueEnd).Trim();
        var at = rest.Length == 0 || rest.StartsWith("//", StringComparison.Ordinal) ? (lineEnd < 0 ? bytes.Length : lineEnd) : valueEnd;
        // A Windows file's line ends with "\r\n": the new line goes before the "\r".
        if (at > valueEnd && bytes[at - 1] == (byte)'\r')
            at--;
        var line = $"\n{Indentation(bytes, lineOf)}{content}";
        return at == valueEnd
            ? Splice(bytes, valueEnd, valueEnd, "," + line)
            : Encoding.UTF8.GetString(bytes, 0, valueEnd) + "," + Encoding.UTF8.GetString(bytes, valueEnd, at - valueEnd) + line +
              Encoding.UTF8.GetString(bytes, at, bytes.Length - at);
    }

    /// <summary>piper.json's <paramref name="text"/> without <paramref name="id"/> in its "packages"; null if it isn't there.</summary>
    public static string? Remove(string text, string id)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var layout = Read(bytes);
        var index = layout.Entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;
        var entries = layout.Entries;
        if (entries.Count == 1)
            return Splice(bytes, layout.PackagesStart!.Value, layout.PackagesEnd!.Value, "{}");
        // With the comma between it and its neighbour: the next one's, or, for the last, the previous one's.
        return index < entries.Count - 1
            ? Splice(bytes, entries[index].NameStart, entries[index + 1].NameStart, "")
            : Splice(bytes, entries[index - 1].ValueEnd, entries[index].ValueEnd, "");
    }

    /// <summary>
    /// The newest version of <paramref name="id"/> in what <c>dotnet package search &lt;id&gt; --exact-match --format
    /// json</c> printed, from every source it searched: stable versions only, unless <paramref name="prerelease"/>.
    /// Null if there's none, with the problems the search reported, if any.
    /// </summary>
    public static (string? Version, IReadOnlyList<string> Problems) Latest(string searchJson, string id, bool prerelease)
    {
        using var document = JsonDocument.Parse(searchJson);
        var root = document.RootElement;
        var problems = root.TryGetProperty("problems", out var reported) && reported.ValueKind == JsonValueKind.Array
            ? reported.EnumerateArray().Select(p => p.TryGetProperty("text", out var message) ? message.GetString() ?? p.ToString() : p.ToString()).ToList()
            : [];
        var versions = new List<string>();
        if (root.TryGetProperty("searchResult", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                if (!source.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
                    continue;
                versions.AddRange(packages.EnumerateArray()
                    .Where(p => p.TryGetProperty("id", out var found) && string.Equals(found.GetString(), id, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.TryGetProperty("version", out var version) || p.TryGetProperty("latestVersion", out version) ? version.GetString() : null)
                    .OfType<string>());
            }
        }
        var newest = versions.Where(v => prerelease || !v.Contains('-')).OrderBy(v => v, VersionOrder.Instance).LastOrDefault();
        return (newest, problems);
    }

    /// <summary>NuGet's order of versions: by number (1.10 after 1.9), a release after its prereleases.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var (a, b) = (Split(x ?? ""), Split(y ?? ""));
            for (int i = 0; i < Math.Max(a.Numbers.Length, b.Numbers.Length); i++)
            {
                var order = (i < a.Numbers.Length ? a.Numbers[i] : 0).CompareTo(i < b.Numbers.Length ? b.Numbers[i] : 0);
                if (order != 0)
                    return order;
            }
            return (a.Label, b.Label) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                var (l, r) => ComparePrerelease(l, r),
            };
        }

        private static (long[] Numbers, string? Label) Split(string version)
        {
            var plain = version.Split('+')[0];
            var dash = plain.IndexOf('-');
            var numbers = (dash < 0 ? plain : plain[..dash]).Split('.').Select(n => long.TryParse(n, out var v) ? v : 0).ToArray();
            return (numbers, dash < 0 ? null : plain[(dash + 1)..]);
        }

        private static int ComparePrerelease(string x, string y)
        {
            var (a, b) = (x.Split('.'), y.Split('.'));
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var order = long.TryParse(a[i], out var m) && long.TryParse(b[i], out var n)
                    ? m.CompareTo(n)
                    : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
                if (order != 0)
                    return order;
            }
            return a.Length.CompareTo(b.Length);
        }
    }

    private static Layout Read(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, Options);
        int rootStart = -1, rootEnd = -1;
        int? lastValueEnd = null, lastName = null, packagesStart = null, packagesEnd = null;
        int currentName = -1;
        bool inPackages = false;
        string? entryId = null;
        int entryName = -1;
        var entries = new List<Entry>();
        while (reader.Read())
        {
            var start = (int)reader.TokenStartIndex;
            var end = start + TokenLength(ref reader);
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject when reader.CurrentDepth == 0:
                    rootStart = start;
                    break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 0:
                    rootEnd = end;
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    currentName = start;
                    if (reader.GetString() == "packages")
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            packagesStart = (int)reader.TokenStartIndex;
                            inPackages = true;
                        }
                        else
                        {
                            reader.Skip();
                            lastName = currentName;
                            lastValueEnd = (int)reader.TokenStartIndex + TokenLength(ref reader);
                        }
                    }
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 2 && inPackages:
                    entryId = reader.GetString();
                    entryName = start;
                    break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 1 && inPackages:
                    packagesEnd = end;
                    inPackages = false;
                    lastName = currentName;
                    lastValueEnd = end;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray when reader.CurrentDepth == 1:
                    lastName = currentName;
                    lastValueEnd = end;
                    break;
                case JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null:
                    if (reader.CurrentDepth == 1)
                    {
                        lastName = currentName;
                        lastValueEnd = end;
                    }
                    else if (reader.CurrentDepth == 2 && inPackages && entryId is not null)
                    {
                        entries.Add(new Entry(entryId, entryName, start, end));
                        entryId = null;
                    }
                    break;
            }
        }
        return new Layout(rootStart, rootEnd, lastValueEnd, lastName, packagesStart, packagesEnd, entries);
    }

    /// <summary>The length in bytes of the token the reader is on, as written (a string's with its quotes).</summary>
    private static int TokenLength(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String or JsonTokenType.PropertyName => reader.ValueSpan.Length + 2,
        JsonTokenType.StartObject or JsonTokenType.EndObject or JsonTokenType.StartArray or JsonTokenType.EndArray => 1,
        _ => reader.ValueSpan.Length,
    };

    /// <summary>The spaces or tabs starting the line <paramref name="offset"/> is on.</summary>
    private static string Indentation(byte[] bytes, int offset)
    {
        var lineStart = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, offset - 1)) + 1;
        var length = 0;
        while (lineStart + length < bytes.Length && bytes[lineStart + length] is (byte)' ' or (byte)'\t')
            length++;
        return Encoding.UTF8.GetString(bytes, lineStart, length);
    }

    private static string Splice(byte[] bytes, int start, int end, string replacement) =>
        Encoding.UTF8.GetString(bytes, 0, start) + replacement + Encoding.UTF8.GetString(bytes, end, bytes.Length - end);

    /// <summary>A JSON string, written plainly (JsonSerializer would escape a version's `+` as `+`).</summary>
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
