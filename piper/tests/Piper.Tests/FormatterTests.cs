using System.Runtime.CompilerServices;
using Piper.Diagnostics;
using Piper.Formatting;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// <c>piper fmt</c> (Piper.Formatting.Formatter): indentation from the brackets and continued lines, blank lines
/// tidied, and nothing else moved, so it can't change what code means.
/// </summary>
public class FormatterTests
{
    private static string Format(string text) =>
        Formatter.Format(new SourceText(text, "main.piper")).Text ?? throw new InvalidOperationException("didn't format");

    private static string RepoDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    public static TheoryData<string> RepoFiles()
    {
        var data = new TheoryData<string>();
        var root = RepoDirectory();
        foreach (var folder in new[] { "samples", "apps", Path.Combine("tests", "Piper.Tests", "Golden") })
        {
            foreach (var file in LoadedProject.SourceFiles(Path.Combine(root, folder)))
                data.Add(Path.GetRelativePath(root, file));
        }
        return data;
    }

    /// <summary>
    /// Over every Piper file in the repository: formatting changes no token (so it can't change what the code means),
    /// and formatting what it formatted changes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(RepoFiles))]
    public void Formatting_keeps_every_token_and_settles_at_once(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoDirectory(), file));
        var once = Format(text);
        Assert.Equal(once, Format(once));
        static IEnumerable<(TokenKind, string)> Tokens(string source) =>
            Lexer.Tokenize(new SourceText(source), new DiagnosticBag()).Select(t => (t.Kind, t.Text));
        Assert.Equal(Tokens(text), Tokens(once));
    }

    [Fact]
    public void Blocks_are_indented_four_spaces_a_level()
    {
        Assert.Equal("""
            fn Main() {
                if true {
                    Print("yes");
                } else {
                    Print("no");
                }
            }

            """, Format("""
            fn Main() {
            if true {
                      Print("yes");
              } else {
            	Print("no");
            }
            }
            """));
    }

    [Fact]
    public void Brackets_opened_on_one_line_indent_once()
    {
        Assert.Equal("""
            let label = Text(Join(
                "a",
                "b"));

            """, Format("""
            let label = Text(Join(
                    "a",
                  "b"));
            """));
    }

    /// <summary>
    /// A continued line is one level in; a chain's first call one level in from what it's called on, and the rest
    /// level with it; and after a multi-line call closes, the chain carries on where it was.
    /// </summary>
    [Fact]
    public void Continued_lines_and_chains_are_indented_in()
    {
        Assert.Equal("""
            fn Top(counts: Dictionary<string, int>) => List<string> =
                counts.Keys
                    .Where(k => match k {
                        "a" => true,
                        _ => false,
                    })
                    .OrderBy(k => k)
                    .ToList();

            record Range(low: int, high: int)
                requires low <= high
                requires high < 100;

            """, Format("""
            fn Top(counts: Dictionary<string, int>) => List<string> =
            counts.Keys
            .Where(k => match k {
            "a" => true,
            _ => false,
            })
            .OrderBy(k => k)
                .ToList();

            record Range(low: int, high: int)
            requires low <= high
                    requires high < 100;
            """));
    }

    /// <summary>A block opened on a continued line holds its body one level in from the statement, not the line.</summary>
    [Fact]
    public void A_block_on_a_continued_line_is_the_statements()
    {
        Assert.Equal("""
            class Handler(options: Options)
                : Base(options) {
                fn Run() { }
            }

            """, Format("""
            class Handler(options: Options)
            : Base(options) {
            fn Run() { }
            }
            """));
    }

    [Fact]
    public void Attributes_and_comments_stay_with_their_code()
    {
        Assert.Equal("""
            [Notify]
            class Model {
                /// What it shows.
                prop Name: string { get; set; } = "";

                // A comment, indented as the code it's in.
                [Command]
                fn Load() { }
            }

            """, Format("""
            [Notify]
            class Model {
            /// What it shows.
            prop Name: string { get; set; } = "";

              // A comment, indented as the code it's in.
            [Command]
                fn Load() { }
            }
            """));
    }

    [Fact]
    public void Blank_lines_are_tidied_and_trailing_whitespace_goes()
    {
        Assert.Equal("fn A() { }\n\nfn B() { }\n", Format("\n\nfn A() { }   \n\n\n\nfn B() { }\n\n\n"));
    }

    [Fact]
    public void Strings_and_markup_keep_their_own_lines()
    {
        // The lines inside a markup literal and a raw string are their text, so they're left exactly as they are.
        var text = "fn Page() => Html =\n    <div>\n  <p>kept</p>\n        </div>;\n\nfn Text() => string = \"\"\"\n      kept\n   as written\n  \"\"\";\n";
        Assert.Equal(text, Format(text));
    }

    [Fact]
    public void Windows_line_endings_are_kept()
    {
        Assert.Equal("fn Main() {\r\n    Print(1);\r\n}\r\n", Format("fn Main() {\r\nPrint(1);\r\n}"));
    }

    [Fact]
    public void A_file_that_doesnt_lex_isnt_formatted()
    {
        var (text, errors) = Formatter.Format(new SourceText("fn Main() { Print(\"open); }", "main.piper"));
        Assert.Null(text);
        Assert.NotEmpty(errors);
    }
}
