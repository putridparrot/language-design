using System.Text;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Args;

/// <summary>
/// Adds a declaration for a program's command-line options, rewritten into ordinary Piper: a record with a field per
/// option, and <c>Parse</c> and <c>FromArgs</c> to make one from <c>Main</c>'s <c>args</c>:
/// <code>
/// args Options "Serves the notes API." {
///     port: int = 5080 "The port to serve on";      // --port 5090 (or --port=5090)
///     verbose: bool "Log each request";             // --verbose, a flag
///     name: string? "Who to greet";                 // optional: None unless given
///     files: List&lt;string&gt; "Files to read";         // the arguments that aren't options
/// }
/// </code>
/// <c>Options.FromArgs(args)</c> gives the options, or prints the usage (on <c>--help</c>) or what's wrong and ends the
/// program; <c>Options.Parse(args)</c> gives a Result instead. Piper.Runtime's PiperArguments does the reading.
/// </summary>
public sealed class ArgsPlugin : IPiperPlugin
{
    private const string Runtime = "Piper.Runtime";

    /// <summary>The types an option can have, by how they're written, and how its value is read.</summary>
    private static readonly Dictionary<string, string> Readers = new()
    {
        ["string"] = "Text",
        ["int"] = "Value<int>",
        ["long"] = "Value<long>",
        ["double"] = "Value<double>",
        ["decimal"] = "Value<decimal>",
    };

    public string Name => "args";

    public IReadOnlyList<PluginKeyword> Keywords { get; } = [new DeclarationKeyword("args", ArgsDeclaration)];

    private sealed record Field(Token Name, TypeSyntax Type, string Kind, bool IsOptional, ExpressionSyntax? Default, ExpressionSyntax? Description);

    /// <summary>
    /// args  = "args" IDENT [ STRING ] "{" { field } "}" ;
    /// field = IDENT ":" type [ "=" expression ] [ STRING ] ";" ;   (* the string describes it in the usage *)
    /// </summary>
    private static IEnumerable<DeclarationSyntax> ArgsDeclaration(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var description = p.Current.Kind == TokenKind.StringLiteral ? p.ParseExpression() : null;
        p.Expect(TokenKind.OpenBrace);

        var fields = new List<Field>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var start = p.Current;
            var fieldName = p.Expect(TokenKind.Identifier);
            p.Expect(TokenKind.Colon);
            var type = p.ParseType();
            ExpressionSyntax? defaultValue = null;
            if (p.Current.Kind == TokenKind.Equals)
            {
                p.NextToken();
                defaultValue = p.ParseExpression();
            }
            var fieldDescription = p.Current.Kind == TokenKind.StringLiteral ? p.ParseExpression() : null;
            p.Expect(TokenKind.Semicolon);
            if (start == p.Current)
                p.NextToken();   // can't happen, but never loop without reading
            if (fieldName.IsMissing)
                continue;

            if (Check(p, fields, fieldName, type, defaultValue) is { } kind)
                fields.Add(new Field(fieldName, type, kind.Kind, kind.IsOptional, defaultValue, fieldDescription));
        }
        p.Expect(TokenKind.CloseBrace);
        return Rewrite(p, name, description, fields);
    }

    /// <summary>What kind of option the field is (a reader's type, "bool", or "positionals"), or null once reported.</summary>
    private static (string Kind, bool IsOptional)? Check(PluginParser p, List<Field> fields, Token name, TypeSyntax type, ExpressionSyntax? defaultValue)
    {
        if (fields.Any(f => OptionName(f.Name.Text) == OptionName(name.Text)))
        {
            p.Report(name.Span, $"'--{OptionName(name.Text)}' is already an option.");
            return null;
        }

        var (inner, optional) = type is OptionalTypeSyntax { Inner: var wrapped } ? (wrapped, true) : (type, false);
        var written = inner is NamedTypeSyntax { Qualifier.IsEmpty: true } named ? named.Name.Text : null;
        var isList = inner is NamedTypeSyntax { Name.Text: "List", TypeArguments: [NamedTypeSyntax { Name.Text: "string", TypeArguments.IsEmpty: true }] } && !optional;

        if (isList)
        {
            if (fields.Any(f => f.Kind == "positionals"))
                p.Report(type.Span, "Only one field can take the arguments that aren't options.");
            else if (defaultValue is not null)
                p.Report(defaultValue.Span, "The arguments that aren't options are an empty list when there are none, so they have no default.");
            else
                return ("positionals", false);
            return null;
        }
        if (written == "bool" && !optional)
        {
            if (defaultValue is null)
                return ("bool", false);
            p.Report(defaultValue.Span, $"A flag is false unless it's given ('--{OptionName(name.Text)}'), so it has no default.");
            return null;
        }
        if (written is null || !Readers.ContainsKey(written) || inner is NamedTypeSyntax { TypeArguments.IsEmpty: false })
        {
            p.Report(type.Span, "An option is a bool (a flag), string, int, long, double or decimal, or one of those made optional with '?'; " +
                                "a List<string> takes the arguments that aren't options.");
            return null;
        }
        if (optional && defaultValue is not null)
        {
            p.Report(defaultValue.Span, "An optional option is None when it isn't given, so it has no default; drop the '?' to give it one.");
            return null;
        }
        return (written, optional);
    }

    /// <summary>
    /// <c>record Options(port: int, …)</c>, and <c>extend Options</c> with <c>Parse</c>, which reads the command line with
    /// PiperArguments, and <c>FromArgs</c>, which ends the program when that fails.
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Rewrite(PluginParser p, Token name, ExpressionSyntax? description, List<Field> fields)
    {
        var holes = new List<(string, object)> { ("name", name) };
        string Hole(string prefix, int index, object value)
        {
            holes.Add(($"{prefix}{index}", value));
            return $"${prefix}{index}";
        }

        var arguments = p.FreshName("arguments");
        holes.Add(("arguments", arguments));
        var recordFields = new List<string>();
        var defaults = new StringBuilder();
        var options = new List<string>();
        var values = new List<string>();
        string? positionals = null;
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var optionName = OptionName(field.Name.Text);
            recordFields.Add($"{Hole("field", i, field.Name.Text)}: {Hole("type", i, field.Type)}");
            if (field.Kind == "positionals")
            {
                positionals = optionName;
                values.Add($"$arguments.Positionals()");
                continue;
            }
            var fieldDescription = field.Description is null ? "None" : Hole("description", i, field.Description);
            if (field.Kind == "bool")
            {
                options.Add($"{Runtime}.PiperOption(\"{optionName}\", \"bool\", {fieldDescription}, false, None)");
                values.Add($"$arguments.Flag(\"{optionName}\")");
                continue;
            }

            // A default is evaluated once, before the command line is read, and shown in the usage.
            string? defaultName = null;
            if (field.Default is not null)
            {
                defaultName = Hole("defaultName", i, p.FreshName(field.Name.Text));
                defaults.Append($"let {defaultName} = {Hole("default", i, field.Default)};\n");
            }
            bool required = field.Default is null && !field.IsOptional;
            options.Add($"{Runtime}.PiperOption(\"{optionName}\", \"{field.Kind}\", {fieldDescription}, {(required ? "true" : "false")}, " +
                        $"{(defaultName is null ? "None" : $"$\"{{{defaultName}}}\"")})");
            var read = $"$arguments.{Readers[field.Kind]}(\"{optionName}\")";
            // A required option that's missing has already made Parse fail; the fallback is never used.
            values.Add(field.IsOptional ? read : $"{read} ?? {defaultName ?? (field.Kind == "string" ? "\"\"" : "0")}");
        }

        var programDescription = description is null ? "None" : "$description";
        if (description is not null)
            holes.Add(("description", description));

        var source = $$"""
            record $name({{string.Join(", ", recordFields)}});

            extend $name {
                static fn Parse(args: string[]) => Result<$name, string> {
                    {{defaults}}
                    let $arguments = {{Runtime}}.PiperArguments.Read(args, {{programDescription}}, [{{string.Join(", ", options)}}], {{(positionals is null ? "None" : $"\"{positionals}\"")}});
                    if $arguments.Help {
                        return Err($arguments.Usage);
                    }
                    if $arguments.Error is Some(error) {
                        return Err($"{error}\n\n{$arguments.Usage}");
                    }
                    Ok($name({{string.Join(", ", values)}}))
                }

                static fn FromArgs(args: string[]) => $name = match $name.Parse(args) {
                    Ok(options) => options,
                    Err(message) => {{Runtime}}.PiperArguments.Exit<$name>(args, message),
                };
            }
            """;
        return p.Template.Declarations(source, [.. holes]);
    }

    /// <summary>How a field is written on the command line: <c>dryRun</c> is <c>--dry-run</c>.</summary>
    private static string OptionName(string field)
    {
        var name = new StringBuilder();
        foreach (var c in field)
        {
            if (char.IsUpper(c) && name.Length > 0)
                name.Append('-');
            name.Append(c == '_' ? '-' : char.ToLowerInvariant(c));
        }
        return name.ToString();
    }
}
