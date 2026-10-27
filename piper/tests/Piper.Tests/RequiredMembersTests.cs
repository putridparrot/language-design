namespace Piper.Tests;

/// <summary>
/// C# 11's <c>required</c> members on .NET types: making one must set them in its initializer, unless the constructor
/// is <c>[SetsRequiredMembers]</c>. No platform type has any, so they come from a library compiled from C#.
/// </summary>
public sealed class RequiredMembersTests : IDisposable
{
    private readonly LibraryPrograms _programs = new();

    public void Dispose() => _programs.Dispose();

    private const string Library = """
        using System.Collections.Generic;
        using System.Diagnostics.CodeAnalysis;
        namespace People;
        public class Person
        {
            public required string Name { get; init; }
            public required int Age { get; set; }
            public string Nick { get; set; } = "";
            public Person() { }
            [SetsRequiredMembers] public Person(string name, int age) { Name = name; Age = age; }
        }
        public class Team
        {
            public required List<string> Members { get; init; }
        }
        public class Entity
        {
            public required string Id { get; init; }
            public Entity() { }
            [SetsRequiredMembers] public Entity(string id) { Id = id; }
        }
        """;

    private const string Prelude = """
        using System.Collections.Generic;
        using People;
        class Named: Entity("fixed") { }
        class Plain: Entity { }
        class Labelled(label: string): Entity("labelled") {
            prop Label: string { get; } = label;
            Labelled(): this("none") { }
        }

        """;

    private CompilationResult Compile(string program) => _programs.Compile("People", Library, Prelude + program);

    [Fact]
    public void Required_members_set_in_an_initializer_or_by_the_constructor_compile_and_run()
    {
        var result = Compile("""
            fn Main() {
                let ada = Person() { Name: "Ada", Age: 36, Nick: "A" };
                let bob = Person("Bob", 40);
                let team = Team() { Members: ["Ada", "Bob"] };
                Print($"{ada.Name} {ada.Age} {bob.Name} {team.Members.Count}");
                // A class whose base constructor sets them sets them too, through any of its constructors.
                Print($"{Named().Id} {Labelled().Id} {Labelled().Label}");
                // One whose base constructor doesn't has them set when it's made.
                Print(Plain() { Id: "plain" }.Id);
            }
            """);
        Assert.Equal("Ada 36 Bob 2\nfixed labelled none\nplain\n", LibraryPrograms.Run(result));
    }

    [Theory]
    [InlineData("PIP0369", "let p = Person();", "'Person' requires 'Name' and 'Age' to be set")]
    [InlineData("PIP0369", "let p = Person() { Name: \"Ada\" };", "'Person' requires 'Age' to be set")]
    [InlineData("PIP0369", "let p = Plain();", "'Plain' requires 'Id' to be set")]
    [InlineData("PIP0370", "let t = Team() { Members: { \"Ada\" } };", "'Members' is required")]
    public void Required_members_left_out_are_reported(string code, string statement, string message)
    {
        var result = Compile($"fn Main() {{ {statement} }}");
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(code, error.Code);
        Assert.Contains(message, error.Message);
    }
}
