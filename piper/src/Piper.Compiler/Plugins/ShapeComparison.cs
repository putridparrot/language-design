using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Piper.Text;

namespace Piper.Plugins;

/// <summary>A change to a recorded shape that breaks what runs against the old one, and where in the source it is.</summary>
public sealed record ShapeBreak(SourceText File, TextSpan Span, string Change)
{
    /// <summary>Where it is, as diagnostics say it: <c>apps/tracker/api/server.piper(12,5)</c>.</summary>
    public string Location
    {
        get
        {
            var (line, column) = File.GetLineColumn(Span.Start);
            return $"{File.FileName}({line},{column})";
        }
    }
}

/// <summary>
/// What <see cref="WireShapeMode.Compare"/> found for one recorded shape (an api's <c>TrackerApi.shape.json</c>, a
/// database's <c>TrackerDb.schema.json</c>): the shape it was compared with (<paramref name="Before"/>: none when it's
/// new), the shape the program has now, and the changes that break what runs against the old one.
/// </summary>
/// <param name="Plugin">The plugin that describes it: api, messaging, workflows, data, …</param>
/// <param name="Path">The recorded shape's full path, beside the declaration.</param>
/// <param name="Unreadable">The recorded shape wasn't JSON Piper could read, so nothing was compared.</param>
public sealed record ShapeComparison(
    string Plugin, string Name, string Path, JsonObject? Before, JsonObject Now, ImmutableArray<ShapeBreak> Breaking, bool Unreadable = false)
{
    public bool IsNew => Before is null && !Unreadable;

    /// <summary>What changed, breaking or not, as <see cref="ShapeChanges.Between"/> lists it.</summary>
    public ImmutableArray<ShapeChange> Changes => Before is null ? [] : ShapeChanges.Between(Before, Now);
}

public enum ShapeChangeKind { Added, Removed, Changed }

/// <summary>One difference between two recorded shapes: <c>Issues.priority</c> added.</summary>
/// <param name="Path">Where, as people name it: the shape's own keys, without the sections that hold them (<c>tables</c>, <c>columns</c>).</param>
public sealed record ShapeChange(ShapeChangeKind Kind, string Path);

/// <summary>
/// The differences between two recorded shapes, whatever plugin describes them, for a report such as <c>piper check</c>'s.
/// Whether each breaks anything is the plugin's to say; this says what changed.
/// </summary>
public static class ShapeChanges
{
    /// <summary>
    /// The keys that only group what a shape holds (an api's <c>calls</c>, a record's <c>fields</c>, a table's
    /// <c>columns</c>), left out of a change's path, so a column added reads <c>Issues.priority</c>, not
    /// <c>tables.Issues.columns.priority</c>.
    /// </summary>
    private static readonly HashSet<string> Sections = ["calls", "types", "tables", "columns", "fields", "variants", "inputs", "messages", "steps"];

    /// <summary>
    /// What was added, removed and changed from <paramref name="before"/> to <paramref name="now"/>: an entry of an object
    /// added or removed is one change (not one per thing inside it); a value or list that differs is changed.
    /// </summary>
    public static ImmutableArray<ShapeChange> Between(JsonObject before, JsonObject now)
    {
        var changes = new List<ShapeChange>();
        Compare(before, now, [], changes);
        return [.. changes];
    }

    private static void Compare(JsonObject before, JsonObject now, ImmutableList<string> path, List<ShapeChange> changes)
    {
        foreach (var (key, oldNode) in before)
        {
            var at = path.Add(key);
            if (!now.ContainsKey(key))
                changes.Add(new ShapeChange(ShapeChangeKind.Removed, Name(at)));
            else if (oldNode is JsonObject oldObject && now[key] is JsonObject newObject)
                Compare(oldObject, newObject, at, changes);
            else if (!JsonNode.DeepEquals(oldNode, now[key]))
                changes.Add(new ShapeChange(ShapeChangeKind.Changed, Name(at)));
        }
        foreach (var (key, _) in now)
        {
            if (!before.ContainsKey(key))
                changes.Add(new ShapeChange(ShapeChangeKind.Added, Name(path.Add(key))));
        }
    }

    /// <summary>
    /// A path as people name it. A shape's own name (<c>"name"</c>) is a value like any other; the sections that group
    /// entries are left out, unless that would leave nothing (a whole section added).
    /// </summary>
    private static string Name(ImmutableList<string> path)
    {
        IReadOnlyList<string> named = [.. path.Where(segment => !Sections.Contains(segment))];
        return string.Join(".", named.Count > 0 ? named : path);
    }
}
