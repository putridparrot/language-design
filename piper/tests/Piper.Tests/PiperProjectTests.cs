using Piper.Text;

namespace Piper.Tests;

/// <summary>piper.json: what a program folder is built into.</summary>
public class PiperProjectTests
{
    private static (PiperProject? Project, System.Collections.Immutable.ImmutableArray<Diagnostics.Diagnostic> Errors) Parse(string text) =>
        PiperProject.Parse(new SourceText(text, "piper.json"));

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("{}")]
    public void An_empty_file_or_object_is_the_default(string text)
    {
        var (project, errors) = Parse(text);
        Assert.Empty(errors);
        Assert.Equal(PiperProject.Empty, project);
    }

    [Fact]
    public void Reads_name_and_output_allowing_comments_and_trailing_commas()
    {
        var (project, errors) = Parse("""
            {
              // what to build
              "name": "greeter",
              "output": "library",
            }
            """);
        Assert.Empty(errors);
        Assert.Equal(new PiperProject("greeter", PiperOutput.Library), project);
    }

    [Theory]
    [InlineData("{ \"packages\": {} }", "Unknown key \"packages\"")]
    [InlineData("{ \"output\": \"dll\" }", "\"output\" must be \"exe\" or \"library\"")]
    [InlineData("{ \"name\": \"\" }", "\"name\" must be a non-empty string")]
    [InlineData("{ \"name\": \"a/b\" }", "\"name\" must be a non-empty string")]
    [InlineData("{ \"name\": 3 }", "\"name\" must be a non-empty string")]
    [InlineData("[]", "must hold a JSON object")]
    [InlineData("{ \"name\": ", "isn't valid JSON")]
    public void Anything_else_is_an_error(string text, string message)
    {
        var (project, errors) = Parse(text);
        Assert.Null(project);
        var error = Assert.Single(errors);
        Assert.Equal("PIP0400", error.Code);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Errors_point_at_the_key()
    {
        var (_, errors) = Parse("{\n  \"name\": \"app\",\n  \"outptu\": \"exe\"\n}");
        var error = Assert.Single(errors);
        Assert.StartsWith("piper.json(3,3): error PIP0400: Unknown key \"outptu\"", error.Format());
        Assert.Equal("\"outptu\"".Length, error.Span.Length);
    }

    [Fact]
    public void Invalid_json_is_reported_where_it_goes_wrong()
    {
        var (_, errors) = Parse("{\n  \"name\": \"app\"\n  \"output\": \"exe\"\n}");
        Assert.StartsWith("piper.json(3,", Assert.Single(errors).Format());
    }
}
