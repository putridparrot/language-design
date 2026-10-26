using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins;

/// <summary>
/// Builds syntax from Piper source with holes, so a plugin can write what its construct means rather than assembling
/// syntax nodes by hand:
/// <code>
/// p.Template.Statement("System.Threading.Tasks.Parallel.ForEach($items, $x => $body);",
///     ("items", items), ("x", name), ("body", block));
/// </code>
/// A hole is <c>$name</c> where a name could go: in an expression, a type, a lambda parameter, or as a statement. Its
/// value can be an expression (or block), a type, a token or a string (a name), or, where a list goes (arguments,
/// collection elements, a block's statements), a sequence of expressions or statements spliced in; for parameters, write
/// <c>fn $name($parameters: unit)</c> with a sequence of <see cref="ParameterSyntax"/>. Where Piper needs a
/// block, write <c>{ $body }</c>: with a block as its value, that's the block itself. A <c>$</c> in a string literal in
/// a template is read as a hole too.
/// <para>
/// The template's own syntax gets the construct's span, so an error in it points at the construct; the user's syntax in
/// the holes keeps its spans, so an error in it points at the user's code. Templates are checked when used: a template
/// that doesn't parse, or a hole without a value, is a bug in the plugin, so it throws.
/// </para>
/// </summary>
public sealed partial class SyntaxTemplate
{
    private const string HolePrefix = "__hole_";

    private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ToImmutableArrayMethod = typeof(SyntaxTemplate).GetMethod(nameof(ToImmutableArray), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> WritableProperties = new();

    private static readonly PropertyInfo GeneratedProperty = typeof(SyntaxNode).GetProperty(nameof(SyntaxNode.IsGenerated))!;

    private readonly Func<TextSpan> _span;

    internal SyntaxTemplate(Func<TextSpan> span) => _span = span;

    /// <summary>A template whose own syntax gets <paramref name="span"/>; plugins use <see cref="PluginParser.Template"/>.</summary>
    public SyntaxTemplate(TextSpan span) : this(() => span)
    {
    }

    public ExpressionSyntax Expression(string template, params (string Name, object Value)[] holes)
    {
        var diagnostics = new DiagnosticBag();
        var expression = Parser.ParseExpression(new SourceText(Prepare(template), "<template>"), diagnostics);
        Check(template, diagnostics);
        return (ExpressionSyntax)Fill([expression], holes)[0];
    }

    public StatementSyntax Statement(string template, params (string Name, object Value)[] holes) =>
        Statements(template, holes) is [var statement]
            ? statement
            : throw new InvalidOperationException($"The template \"{template}\" isn't a single statement.");

    /// <summary>Statements; a final expression without a `;` is a statement too.</summary>
    public ImmutableArray<StatementSyntax> Statements(string template, params (string Name, object Value)[] holes)
    {
        var function = (FunctionDeclarationSyntax)Declarations($"fn __template() {{\n{template}\n}}", template, holes).Single();
        var body = function.BlockBody!;
        return body.Trailing is null ? body.Statements : body.Statements.Add(new ExpressionStatementSyntax(body.Trailing.Span, body.Trailing));
    }

    public ImmutableArray<DeclarationSyntax> Declarations(string template, params (string Name, object Value)[] holes) =>
        Declarations(template, template, holes);

    /// <param name="shown">The template as the plugin wrote it, for error messages.</param>
    private ImmutableArray<DeclarationSyntax> Declarations(string template, string shown, (string Name, object Value)[] holes)
    {
        var diagnostics = new DiagnosticBag();
        var unit = Parser.ParseCompilationUnit(new SourceText(Prepare(template), "<template>"), diagnostics);
        Check(shown, diagnostics);
        return [.. Fill(unit.Declarations, holes).Cast<DeclarationSyntax>()];
    }

    private static string Prepare(string template) => HoleRegex().Replace(template, HolePrefix + "$1");

    private static void Check(string template, DiagnosticBag diagnostics)
    {
        if (diagnostics.HasErrors)
            throw new InvalidOperationException($"The template \"{template}\" has errors: {string.Join(" ", diagnostics.Select(d => d.Message))}");
    }

    private List<SyntaxNode> Fill(IEnumerable<SyntaxNode> nodes, (string Name, object Value)[] holes)
    {
        var values = holes.ToDictionary(hole => HolePrefix + hole.Name, hole => hole.Value);
        var span = _span();
        var used = new HashSet<string>();
        var filled = nodes.Select(node => (SyntaxNode)Rewrite(node, typeof(SyntaxNode), values, used, span)!).ToList();
        if (values.Keys.FirstOrDefault(name => !used.Contains(name)) is { } unused)
            throw new InvalidOperationException($"The template has no hole ${unused[HolePrefix.Length..]}.");
        return filled;
    }

    /// <summary>
    /// The hole a list item is, which a sequence can be spliced into: as <see cref="HoleName"/>, or a parameter named by
    /// a hole (<c>fn f($parameters: unit)</c>, as a parameter needs a type to parse; the type is ignored).
    /// </summary>
    private static string? SpliceHoleName(object? item) => HoleName(item) ?? item switch
    {
        ParameterSyntax { Name: var name } => HoleName(name),
        LambdaParameterSyntax { Name: var name } => HoleName(name),
        _ => null,
    };

    /// <summary>The hole a node is, if it's one: a name, a type name, or a statement that's just a name.</summary>
    private static string? HoleName(object? node) => node switch
    {
        NameExpressionSyntax { Identifier.Text: var text, TypeArguments.IsEmpty: true } when text.StartsWith(HolePrefix, StringComparison.Ordinal) => text,
        NamedTypeSyntax { Qualifier.IsEmpty: true, TypeArguments.IsEmpty: true, Name.Text: var text } when text.StartsWith(HolePrefix, StringComparison.Ordinal) => text,
        ExpressionStatementSyntax { Expression: var expression } => HoleName(expression),
        Token { Kind: TokenKind.Identifier, Text: var text } when text.StartsWith(HolePrefix, StringComparison.Ordinal) => text,
        _ => null,
    };

    private static object? Rewrite(object? node, Type type, Dictionary<string, object> values, HashSet<string> used, TextSpan span)
    {
        if (HoleName(node) is { } hole)
        {
            if (!values.TryGetValue(hole, out var value))
                throw new InvalidOperationException($"The template's hole ${hole[HolePrefix.Length..]} has no value.");
            used.Add(hole);
            return HoleValue(value, node!, type, hole);
        }

        switch (node)
        {
            case Token token:
                return token with { Span = span, IsGenerated = true };
            case BlockExpressionSyntax { Statements.IsEmpty: true, Trailing: var trailing } when HoleName(trailing) is { } name &&
                                                                                          values.GetValueOrDefault(name) is BlockExpressionSyntax block:
                // `if c { $body }`: a block on its own in a block is that block (a hole can't be where a `{` must be).
                used.Add(name);
                return block;
            case BlockExpressionSyntax { Trailing: var trailing } block when HoleName(trailing) is { } name &&
                                                                         values.GetValueOrDefault(name) is IEnumerable<StatementSyntax> statements:
            {
                // `{ a(); $rest }`: statements in a block's last place are spliced in, rather than being its value.
                used.Add(name);
                var rewritten = (BlockExpressionSyntax)Rewrite(block with { Trailing = null }, type, values, used, span)!;
                return rewritten with { Statements = rewritten.Statements.AddRange(statements) };
            }
            case SyntaxNode syntax:
            {
                var clone = CloneMethod.Invoke(syntax, null)!;
                foreach (var property in WritableProperties.GetOrAdd(syntax.GetType(), WritablePropertiesOf))
                {
                    var value = property.GetValue(syntax);
                    property.SetValue(clone, property.PropertyType == typeof(TextSpan)
                        ? span
                        : Rewrite(value, property.PropertyType, values, used, span));
                }
                GeneratedProperty.SetValue(clone, true);
                return clone;
            }
            case IEnumerable when node.GetType().GetProperty(nameof(ImmutableArray<int>.IsDefault))?.GetValue(node) is true:
                return node;
            case IEnumerable items when type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>):
            {
                var elementType = type.GetGenericArguments()[0];
                var result = new List<object?>();
                foreach (var item in items)
                {
                    // `f(a, $rest)`, `[$items]`, `{ $statements; b(); }`: a sequence in a list's place is spliced in.
                    if (SpliceHoleName(item) is { } name && values.GetValueOrDefault(name) is IEnumerable sequence and not SyntaxNode and not string)
                    {
                        used.Add(name);
                        foreach (var element in sequence)
                            result.Add(elementType.IsInstanceOfType(element) || element is not ExpressionSyntax expression
                                ? element
                                : new ExpressionStatementSyntax(expression.Span, expression));
                    }
                    else
                    {
                        result.Add(Rewrite(item, elementType, values, used, span));
                    }
                }
                return ToImmutableArrayMethod.MakeGenericMethod(elementType).Invoke(null, [result]);
            }
            default:
                return node;
        }
    }

    /// <summary>A hole's value, converted to what its place needs: a name's token, or an expression statement.</summary>
    private static object HoleValue(object value, object hole, Type type, string name)
    {
        object converted = (value, hole) switch
        {
            (string text, Token token) => token with { Text = text },
            (string text, NameExpressionSyntax expression) => expression with { Identifier = expression.Identifier with { Text = text } },
            (Token token, NameExpressionSyntax) => new NameExpressionSyntax(token.Span, token),
            (Token token, NamedTypeSyntax) => new NamedTypeSyntax(token.Span, [], token, []),
            (ExpressionSyntax expression, ExpressionStatementSyntax) => new ExpressionStatementSyntax(expression.Span, expression),
            (NameExpressionSyntax expression, Token) => expression.Identifier,
            _ => value,
        };
        return type.IsInstanceOfType(converted)
            ? converted
            : throw new InvalidOperationException($"The template's hole ${name[HolePrefix.Length..]} needs a {type.Name}, not a {value.GetType().Name}.");
    }

    private static PropertyInfo[] WritablePropertiesOf(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)];

    private static ImmutableArray<T> ToImmutableArray<T>(List<object?> items) => [.. items.Cast<T>()];

    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex HoleRegex();
}
