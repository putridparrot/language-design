using System.Text.Json.Nodes;
using Piper.Plugins;

namespace Piper.Tests;

/// <summary>What changed between two recorded shapes, as <c>piper check</c> lists it, whichever plugin recorded them.</summary>
public class ShapeChangesTests
{
    private static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    [Fact]
    public void A_databases_changes_are_named_by_table_and_column()
    {
        var before = Json("""
            { "name": "TrackerDb", "tables": {
                "Issues": { "columns": { "id": { "type": "int" }, "title": { "type": "string" } }, "indexes": [["id"]] },
                "Notes": { "columns": { "id": { "type": "int" } } } },
              "piper": ["outbox"] }
            """);
        var now = Json("""
            { "name": "TrackerDb", "tables": {
                "Issues": { "columns": { "id": { "type": "int" }, "title": { "type": "string?" }, "priority": { "type": "int?" } }, "indexes": [["id"], ["priority"]] },
                "Tags": { "columns": { "id": { "type": "int" } } } },
              "piper": ["outbox"] }
            """);

        Assert.Equal(
            [
                new ShapeChange(ShapeChangeKind.Changed, "Issues.title.type"),
                new ShapeChange(ShapeChangeKind.Added, "Issues.priority"),
                new ShapeChange(ShapeChangeKind.Changed, "Issues.indexes"),
                new ShapeChange(ShapeChangeKind.Removed, "Notes"),
                new ShapeChange(ShapeChangeKind.Added, "Tags"),
            ],
            ShapeChanges.Between(before, now).ToArray());
    }

    [Fact]
    public void The_same_shape_has_no_changes()
    {
        const string shape = """{ "name": "Shelf", "calls": { "GET /api/books": { "name": "Books", "inputs": {}, "output": "List<Book>" } }, "types": {} }""";
        Assert.Empty(ShapeChanges.Between(Json(shape), Json(shape)));
    }

    [Fact]
    public void A_whole_section_added_is_named_as_the_section()
    {
        Assert.Equal([new ShapeChange(ShapeChangeKind.Added, "types")], ShapeChanges.Between(Json("""{ "name": "A" }"""), Json("""{ "name": "A", "types": {} }""")).ToArray());
    }
}
