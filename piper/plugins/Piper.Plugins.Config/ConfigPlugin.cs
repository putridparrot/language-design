using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Config;

/// <summary>
/// Typed configuration: <c>config Smtp from "Smtp" { host: string; port: int = 587 requires port &gt; 0; }</c> becomes a
/// record and functions loading it from an <c>IConfiguration</c> (appsettings.json, environment variables, the command
/// line…), written in ordinary Piper with no reflection:
/// <list type="bullet">
/// <item><c>record Smtp(host: string, port: int) requires port &gt; 0</c>.</item>
/// <item><c>fn LoadSmtp(configuration) =&gt; Result&lt;Smtp, List&lt;string&gt;&gt;</c>, reading the "Smtp" section, which
/// reports every problem at once, each naming its key: a missing value, one that doesn't parse, a broken invariant.</item>
/// <item><c>fn SmtpFrom(configuration) =&gt; Smtp</c>, which throws with all of them: the fail-fast form for startup.</item>
/// </list>
/// </summary>
public sealed class ConfigPlugin : IPiperPlugin
{
    private const string Configuration = "Microsoft.Extensions.Configuration.IConfiguration";
    private const string Invariant = "System.Globalization.CultureInfo.InvariantCulture";

    public string Name => "config";

    /// <summary>Reading appsettings.json and environment variables. An ASP.NET Core program has them in its framework.</summary>
    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>
    {
        ["Microsoft.Extensions.Configuration.Json"] = "10.0.0",
        ["Microsoft.Extensions.Configuration.EnvironmentVariables"] = "10.0.0",
    };

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("config", Config)
        {
            Description = "Declares typed configuration: a record, with `fn LoadName(configuration) => Result<Name, List<string>>`, which reports every missing or invalid value with its key, " +
                          "and `fn NameFrom(configuration) => Name`, which throws with them. Fields may have defaults, `requires` clauses, `T?` for optional values, `List<T>`, and another config's type for a nested section.",
            Snippet = "config ${1:Settings} from \"${2:Settings}\" {\n\t${3:port}: ${4:int} = ${5:8080};\n\t$0\n}",
            Words = new Dictionary<string, string>
            {
                ["from"] = "The configuration section it's read from, such as \"Smtp\" for the keys `Smtp:Host`, or `Smtp__Host` in the environment. Without it, the config's own name.",
            },
        },
    ];

    /// <summary>How a scalar type's value is read from its text, and the value a required one starts with.</summary>
    private sealed record Scalar(string TryParse, string Placeholder);

    /// <summary>The types a value can be: each parsed as its <c>TryParse</c> does, numbers and dates in the invariant culture.</summary>
    private static readonly Dictionary<string, Scalar> Scalars = new()
    {
        ["string"] = new("", "\"\""),
        ["int"] = new($"int.TryParse($text, System.Globalization.NumberStyles.Integer, {Invariant}, out let $value)", "0"),
        ["long"] = new($"long.TryParse($text, System.Globalization.NumberStyles.Integer, {Invariant}, out let $value)", "0L"),
        ["double"] = new($"double.TryParse($text, System.Globalization.NumberStyles.Float, {Invariant}, out let $value)", "0.0"),
        ["decimal"] = new($"decimal.TryParse($text, System.Globalization.NumberStyles.Number, {Invariant}, out let $value)", "0m"),
        ["bool"] = new("bool.TryParse($text, out let $value)", "false"),
        ["Guid"] = new("System.Guid.TryParse($text, out let $value)", "System.Guid.Empty"),
        ["TimeSpan"] = new($"System.TimeSpan.TryParse($text, {Invariant}, out let $value)", "System.TimeSpan.Zero"),
        ["DateTime"] = new($"System.DateTime.TryParse($text, {Invariant}, System.Globalization.DateTimeStyles.RoundtripKind, out let $value)", "System.DateTime.MinValue"),
    };

    /// <summary>
    /// A field: <c>port: int = 587 requires port &gt; 0;</c>. A <paramref name="Secret"/> one (<c>secret password: string;</c>)
    /// is read as its type, then kept as a <c>Piper.Runtime.Secret</c>, which never shows its value.
    /// </summary>
    private sealed record Field(Token Name, TypeSyntax Type, ExpressionSyntax? Default, ImmutableArray<ExpressionSyntax> Requires, bool Secret = false);

    /// <summary>
    /// config = "config" IDENT [ "from" STRING ] "{" { field } "}" ;
    /// field  = [ "secret" ] IDENT ":" type [ "=" expression ] { "requires" expression { "," expression } } ";" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Config(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        ExpressionSyntax section = p.Template.Expression("$section", ("section", Text(name.Span, name.Text)));
        if (p.AtWord("from"))
        {
            p.ReadWord();
            var token = p.Expect(TokenKind.StringLiteral);
            section = new LiteralExpressionSyntax(token.Span, token, token.Value ?? "");
        }
        p.Expect(TokenKind.OpenBrace);

        var fields = new List<Field>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            // `secret password: string;`, not a field called `secret`, which would be followed by `:`.
            var secret = p.AtWord("secret") && p.Peek(1).Kind == TokenKind.Identifier ? p.ReadWord() : null;
            var field = p.Expect(TokenKind.Identifier);
            p.Expect(TokenKind.Colon);
            var type = p.ParseType();
            ExpressionSyntax? @default = null;
            if (p.Current.Kind == TokenKind.Equals)
            {
                p.NextToken();
                @default = p.ParseExpression();
            }
            var requires = ImmutableArray.CreateBuilder<ExpressionSyntax>();
            while (p.Current is { Kind: TokenKind.Identifier, Text: "requires" })
            {
                p.NextToken();
                requires.Add(p.ParseExpression());
                while (p.Current.Kind == TokenKind.Comma)
                {
                    p.NextToken();
                    requires.Add(p.ParseExpression());
                }
            }
            p.Expect(TokenKind.Semicolon);
            if (field.IsMissing)
                continue;
            if (fields.Any(f => f.Name.Text == field.Text))
                p.Report(field.Span, $"'{field.Text}' is already a field of {name.Text}.");
            else if (Kind(type) is null)
                p.Report(type.Span, $"A configuration value can't be read as this type. It can be {string.Join(", ", Scalars.Keys)}, one of those optional (T?) or in a List<T>, or another config, for a nested section.");
            else if (secret is not null && Kind(type) is not ("scalar" or "optional"))
                p.Report(secret.Span, $"A secret is one value, such as 'secret {field.Text}: string;' or 'string?': not a list or a section.");
            else
                fields.Add(new Field(field, type, @default, requires.ToImmutable(), secret is not null));
        }
        p.Expect(TokenKind.CloseBrace);

        var record = (RecordDeclarationSyntax)p.Template.Declarations("record $name($fields: unit);",
            ("name", name), ("fields", fields.Select(f => new ParameterSyntax(f.Name.Span, f.Name, f.Secret ? SecretType(f.Type) : f.Type)).ToList())).Single();
        record = record with { Requires = [.. fields.SelectMany(f => f.Requires)] };

        var section_ = p.FreshName("section");
        var path = p.FreshName("path");
        var errors = p.FreshName("errors");
        var statements = new List<StatementSyntax>();
        var locals = new List<ExpressionSyntax>();
        var nested = new List<(Token Result, Token Value)>();
        foreach (var field in fields)
        {
            statements.AddRange(Read(p, field, section_, path, errors, locals, nested));
            // Read as its type, then kept as a Secret.
            if (field.Secret)
                locals[^1] = field.Type is OptionalTypeSyntax { Inner: var inner }
                    ? p.Template.Expression("match $value { Some($x) => Some(Piper.Runtime.Secret<$T>($x)), None => None }",
                        ("value", locals[^1]), ("x", p.FreshName("value")), ("T", inner))
                    : p.Template.Expression("Piper.Runtime.Secret<$T>($value)", ("value", locals[^1]), ("T", field.Type));
        }

        // Every problem has been noted; a nested section's value is there when none was.
        var finish = new List<StatementSyntax> { Statement(p, "if $errors.Count > 0 { return Err($errors); }", ("errors", errors)) };
        foreach (var (result, value) in nested)
            finish.Add(Statement(p, "let Ok($value) = $result else { return Err($errors); };", ("value", value), ("result", result), ("errors", errors)));
        // The record's invariants, reported as the others are, with the section's path.
        finish.Add(Statement(p, """
            try {
                return Ok($name($locals));
            } catch (e: System.ArgumentException) {
                $errors.Add($path + ": " + e.Message);
            }
            """, ("name", name), ("locals", locals), ("errors", errors), ("path", path)));

        var functions = p.Template.Declarations($$"""
            fn $sectionLoader($section: {{Configuration}}, $path: string) => Result<$name, System.Collections.Generic.List<string>> {
                let $errors: System.Collections.Generic.List<string> = [];
                $statements;
                $finish;
                Err($errors)
            }

            fn $loader(configuration: {{Configuration}}) => Result<$name, System.Collections.Generic.List<string>> =
                $sectionLoader(configuration.GetSection($sectionName), $sectionName);

            fn $from(configuration: {{Configuration}}) => $name = match $loader(configuration) {
                Ok(value) => value,
                Err(problems) => throw System.InvalidOperationException("The configuration has problems:\n" + string.Join("\n", problems)),
            };
            """,
            ("sectionLoader", SectionLoader(name.Text)), ("section", section_), ("path", path), ("name", name), ("errors", errors),
            ("statements", statements), ("finish", finish), ("loader", $"Load{name.Text}"), ("sectionName", section), ("from", $"{name.Text}From"));
        return [record, .. functions];
    }

    /// <summary>A secret field's type: <c>Piper.Runtime.Secret&lt;string&gt;</c>, or an Option of one for <c>string?</c>.</summary>
    private static TypeSyntax SecretType(TypeSyntax type)
    {
        TypeSyntax Wrap(TypeSyntax inner)
        {
            Token Name(string text) => new(TokenKind.Identifier, inner.Span, text) { IsGenerated = true };
            return new NamedTypeSyntax(inner.Span, [Name("Piper"), Name("Runtime")], Name("Secret"), [inner]);
        }
        return type is OptionalTypeSyntax optional ? new OptionalTypeSyntax(type.Span, Wrap(optional.Inner)) : Wrap(type);
    }

    /// <summary>The function reading one section into a config, which a config with a field of its type calls for a nested section.</summary>
    private static string SectionLoader(string config) => $"Load{config}Section";

    /// <summary>What a field's type is read as: a scalar, an optional or list of one, or a nested config; null if none.</summary>
    private static string? Kind(TypeSyntax type) => type switch
    {
        NamedTypeSyntax { TypeArguments.IsEmpty: true, Name.Text: var scalar } when Scalars.ContainsKey(scalar) => "scalar",
        OptionalTypeSyntax { Inner: NamedTypeSyntax { TypeArguments.IsEmpty: true, Name.Text: var scalar } } when Scalars.ContainsKey(scalar) => "optional",
        NamedTypeSyntax { Name.Text: "List", TypeArguments: [NamedTypeSyntax { TypeArguments.IsEmpty: true, Name.Text: var element }] } when Scalars.ContainsKey(element) => "list",
        NamedTypeSyntax { Qualifier.IsEmpty: true, TypeArguments.IsEmpty: true } => "nested",
        _ => null,
    };

    /// <summary>The statements reading <paramref name="field"/> into a local, added to <paramref name="locals"/>, noting problems in the errors.</summary>
    private static IEnumerable<StatementSyntax> Read(PluginParser p, Field field, Token section, Token path, Token errors,
        List<ExpressionSyntax> locals, List<(Token Result, Token Value)> nested)
    {
        var local = p.FreshName(field.Name.Text);
        var key = Text(field.Name.Span, field.Name.Text);
        var keyPath = Text(field.Name.Span, ":" + field.Name.Text);
        var common = new List<(string, object)> { ("local", local), ("section", section), ("key", key), ("path", path), ("keyPath", keyPath), ("errors", errors) };

        switch (Kind(field.Type))
        {
            case "nested":
            {
                // Another config, read from the section under this key; its problems are this one's.
                var result = p.FreshName(field.Name.Text + "Result");
                var nestedErrors = p.FreshName("problems");
                var value = p.FreshName(field.Name.Text);
                nested.Add((result, value));
                locals.Add(p.Template.Expression("$value", ("value", value)));
                yield return Statement(p, $"let $result = {SectionLoader(((NamedTypeSyntax)field.Type).Name.Text)}($section.GetSection($key), $path + $keyPath);",
                    ("result", result), ("section", section), ("key", key), ("path", path), ("keyPath", keyPath));
                yield return Statement(p, "if $result is Err($problems) { $errors.AddRange($problems); }",
                    ("result", result), ("problems", nestedErrors), ("errors", errors));
                yield break;
            }
            case "list":
            {
                // A section's children, `Recipients:0`, `Recipients:1`, …; with none, the default (or an empty list).
                var element = ((NamedTypeSyntax)((NamedTypeSyntax)field.Type).TypeArguments[0]).Name.Text;
                var child = p.FreshName("child");
                var text = p.FreshName("text");
                var found = p.FreshName("found");
                locals.Add(p.Template.Expression("$local", ("local", local)));
                yield return Statement(p, "mut $local: $T = $initial;",
                    ("local", local), ("T", field.Type), ("initial", field.Default ?? p.Template.Expression("[]")));
                yield return Statement(p, "mut $found = false;", ("found", found));
                var add = ParseInto(p, element, text, "$local.Add($value);", common, keyPath, child);
                yield return Statement(p, """
                    for $child in $section.GetSection($key).GetChildren() {
                        if !$found { $local = []; $found = true; }
                        if $child.Value is Some($text) { $add }
                    }
                    """, [.. common, ("child", child), ("text", text), ("found", found), ("add", add)]);
                yield break;
            }
        }

        // A scalar, optional or not.
        bool optional = field.Type is OptionalTypeSyntax;
        var typeName = ((NamedTypeSyntax)(optional ? ((OptionalTypeSyntax)field.Type).Inner : field.Type)).Name.Text;
        var initial = field.Default
            ?? (optional ? p.Template.Expression("None") : p.Template.Expression(Scalars[typeName].Placeholder));
        var textName = p.FreshName("text");
        locals.Add(p.Template.Expression("$local", ("local", local)));
        yield return Statement(p, "mut $local: $T = $initial;", ("local", local), ("T", field.Type), ("initial", initial));
        var assign = ParseInto(p, typeName, textName, optional ? "$local = Some($value);" : "$local = $value;", common, keyPath, null);
        // Missing: fine for an optional or a field with a default, a problem otherwise.
        var missing = optional || field.Default is not null
            ? Statements(p, "")
            : Statements(p, "$errors.Add($path + $keyPath + \" is missing\");", ("errors", errors), ("path", path), ("keyPath", keyPath));
        yield return Statement(p, """
            if $section[$key] is Some($text) { $assign } else { $missing }
            """, [.. common, ("text", textName), ("assign", assign), ("missing", missing)]);
    }

    /// <summary>
    /// Statements putting <paramref name="text"/>, parsed as <paramref name="type"/>, through <paramref name="use"/>
    /// (which names it <c>$value</c>), or noting that it doesn't parse.
    /// </summary>
    private static ImmutableArray<StatementSyntax> ParseInto(PluginParser p, string type, Token text, string use,
        List<(string, object)> common, ExpressionSyntax keyPath, Token? child)
    {
        var value = p.FreshName("value");
        var holes = new List<(string, object)>(common) { ("text", text), ("value", value) };
        if (type == "string")
            return Statements(p, use.Replace("$value", "$text"), [.. holes]);
        // `Smtp:Port: "abc" isn't an int` (for a list's element, its index: `Smtp:Ports:2`).
        var where = child is null ? "$path + $keyPath" : "$path + $keyPath + \":\" + $child.Key";
        if (child is not null)
            holes.Add(("child", child));
        return Statements(p, $$"""
            if {{Scalars[type].TryParse}} {
                {{use}}
            } else {
                $errors.Add({{where}} + ": \"" + $text + "\" isn't a valid {{type}}");
            }
            """, [.. holes]);
    }

    /// <summary>
    /// A template's statement, given only the holes it has: the reading code shares a set of holes (the section, the key,
    /// the errors…) that not every template uses, and a template given a hole it lacks is a bug.
    /// </summary>
    private static StatementSyntax Statement(PluginParser p, string template, params (string Name, object Value)[] holes) =>
        p.Template.Statement(template, Used(template, holes));

    private static ImmutableArray<StatementSyntax> Statements(PluginParser p, string template, params (string Name, object Value)[] holes) =>
        p.Template.Statements(template, Used(template, holes));

    private static (string Name, object Value)[] Used(string template, (string Name, object Value)[] holes) =>
        [.. holes.Where(h => System.Text.RegularExpressions.Regex.IsMatch(template, $@"\${h.Name}(?![A-Za-z0-9_])"))];

    /// <summary>A string literal, placed at <paramref name="span"/>.</summary>
    private static LiteralExpressionSyntax Text(TextSpan span, string text)
    {
        var token = new Token(TokenKind.StringLiteral, span, "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", text) { IsGenerated = true };
        return new LiteralExpressionSyntax(span, token, text);
    }
}
