using System.Collections.Immutable;
using System.Reflection;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Strings of a kind: <c>sql$"select * from lots where id = {id}"</c> makes a <c>Piper.Runtime.Sql</c>, whose holes are
/// parameters. A kind is a .NET type marked <c>[InterpolationKind("sql")]</c>; the literal is its builder's calls,
/// <c>Sql.Builder().Text("select … id = ").Hole(id).Build()</c>, so each hole is checked against the builder's
/// <c>Hole</c> overloads, and what can't go in one is an error at the hole. As C# target-types <c>$"…"</c>, a plain
/// interpolated string given where a kind is wanted (a parameter, a variable's type) is one of that kind too.
/// </summary>
public sealed partial class Binder
{
    private const string InterpolationKindAttribute = "Piper.Runtime.InterpolationKindAttribute";

    /// <summary>Kinds that need no `using`: Piper.Runtime's own.</summary>
    private static readonly Type[] BuiltinKinds =
        [.. typeof(Piper.Runtime.Sql).Assembly.GetExportedTypes().Where(t => KindPrefix(t) is not null)];

    /// <summary>The prefix <paramref name="type"/> is written with (<c>sql</c>), if it's a kind.</summary>
    private static string? KindPrefix(Type type) => KindAttribute(type)?.ConstructorArguments is [{ Value: string prefix }] ? prefix : null;

    private static CustomAttributeData? KindAttribute(Type type)
    {
        try
        {
            return type.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == InterpolationKindAttribute);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return null;
        }
    }

    private static string? KindHolds(Type type) =>
        KindAttribute(type)?.NamedArguments.FirstOrDefault(a => a.MemberName == "Holds").TypedValue.Value as string;

    /// <summary>Whether a value of <paramref name="type"/> can be made from an interpolated string.</summary>
    private static bool IsInterpolationKind(TypeSymbol type) => ClrTypes.ToClr(type) is { } clr && KindPrefix(clr) is not null;

    private static bool IsFormattableString(TypeSymbol type) => ClrTypes.ToClr(type) == typeof(FormattableString);

    /// <summary>The kind <c>prefix$"…"</c> makes: Piper.Runtime's, or one in a namespace this file imports.</summary>
    private TypeSymbol? FindInterpolationKind(string prefix, TextSpan span)
    {
        var imported = _file.Usings.Append(_file.Namespace)
            .Where(n => !n.IsGlobal)
            .Select(n => n.FullName)
            .Distinct()
            .SelectMany(n => _globalNamespace.Clr.TypesIn(n));
        var found = BuiltinKinds.Concat(imported)
            .Where(t => KindPrefix(t) == prefix)
            .DistinctBy(t => t.FullName)
            .ToList();
        switch (found.Count)
        {
            case 0:
                _diagnostics.ReportUnknownInterpolationKind(span, prefix);
                return null;
            case 1:
                return ClrTypeSymbol.For(found[0]);
            default:
                _diagnostics.ReportAmbiguousInterpolationKind(span, prefix, found.Select(t => t.FullName ?? t.Name));
                return null;
        }
    }

    private BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax syntax, TypeSymbol? expected = null)
    {
        var parts = syntax.Parts.Select(part => part switch
        {
            InterpolatedTextSyntax text => new BoundLiteralExpression(text.Text, TypeSymbol.String),
            InterpolationSyntax { Alignment: null, Format: null } hole => Hole(hole),
            InterpolationSyntax hole => new BoundFormattedExpression(Hole(hole), hole.Alignment, hole.Format),
            _ => throw new InvalidOperationException(),
        });

        // A Secret in a hole is refused: a string is how one usually leaks (into a log, a message, a page).
        BoundExpression Hole(InterpolationSyntax hole)
        {
            var value = RequireValue(BindExpression(hole.Expression), hole.Expression.Span);
            if (IsSecret(value.Type))
                _diagnostics.ReportSecretInString(hole.Expression.Span);
            return value;
        }
        var bound = new BoundInterpolatedStringExpression([.. parts]) { Syntax = syntax };

        if (syntax.Prefix is { } prefix)
            return FindInterpolationKind(prefix, syntax.PrefixSpan) is { } kind ? BindAsKind(bound, kind) : new BoundErrorExpression();
        // `let query: Sql = $"…"`, as C# target-types an interpolated string.
        if (expected is not null && IsInterpolationKind(expected))
            return BindAsKind(bound, expected);
        return bound;
    }

    /// <summary>Piper.Runtime's <c>Secret&lt;T&gt;</c>, or an Option of one.</summary>
    private static bool IsSecret(TypeSymbol type) =>
        (type.IsOption ? type.TypeArguments[0] : type) is ClrTypeSymbol { Type: { IsGenericType: true } clr } &&
        clr.GetGenericTypeDefinition().FullName == "Piper.Runtime.Secret`1";

    /// <summary>
    /// An interpolated string, already bound as text, as a value of <paramref name="kind"/>: its builder's calls, with
    /// each hole checked against the builder's <c>Hole</c> overloads.
    /// </summary>
    private BoundExpression BindAsKind(BoundInterpolatedStringExpression interpolated, TypeSymbol kind)
    {
        var syntax = interpolated.Syntax!;
        var clr = ClrTypes.ToClr(kind)!;
        var prefix = syntax.Prefix ?? KindPrefix(clr)!;
        if (clr.GetMethod("Builder", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes) is not { } start)
        {
            _diagnostics.ReportInvalidInterpolationKind(syntax.Span, kind, "public static Builder()");
            return new BoundErrorExpression();
        }

        BoundExpression builder = BindClrInvocation(null, kind, [start], $"{kind}.Builder", [], [], syntax.Span, new Dictionary<Type, TypeSymbol>());
        bool failed = builder is BoundErrorExpression;
        BoundExpression Call(string method, ImmutableArray<BoundExpression> arguments, TextSpan span)
        {
            if (failed)
                return builder;
            var (builderClr, map) = ClrContext(builder.Type);
            var methods = InstanceMethods(builderClr).Where(m => m.Name == method).ToList();
            if (methods.Count == 0)
            {
                _diagnostics.ReportInvalidInterpolationKind(syntax.Span, kind, $"{method} method on its builder ('{builder.Type}')");
                failed = true;
                return builder;
            }
            return BindClrInvocation(builder, builder.Type, methods, method, [], arguments, span, map);
        }

        bool holeErrors = false;
        for (int i = 0; i < interpolated.Parts.Length && !failed; i++)
        {
            var part = interpolated.Parts[i];
            var span = syntax.Parts[i] is InterpolationSyntax hole ? hole.Expression.Span : syntax.Parts[i].Span;
            if (part is BoundLiteralExpression { Value: string text } && syntax.Parts[i] is InterpolatedTextSyntax)
            {
                builder = Call("Text", [part], span);
                continue;
            }

            var (value, alignment, format) = part is BoundFormattedExpression f ? (f.Value, f.Alignment, f.Format) : (part, null, null);
            if (value.Type == TypeSymbol.Error)
            {
                failed = true;
                break;
            }
            if (alignment is not null)
            {
                _diagnostics.ReportInterpolationKindFormat(syntax.Parts[i].Span, prefix, alignment: true);
                holeErrors = true;
                continue;
            }
            // A newtype goes in as its value: `{lot.id}`, a LotId of an int, is the int.
            if (value.Type.OriginalDefinition is RecordSymbol { IsNewtype: true, Fields: [var field] })
                value = new BoundFieldAccessExpression(value, field, field.Type);
            ImmutableArray<BoundExpression> arguments = format is null ? [value] : [value, new BoundLiteralExpression(format, TypeSymbol.String)];

            BoundExpression next = null!;
            var diagnostics = TrialDiagnostics(() => next = Call("Hole", arguments, span));
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) || next is BoundErrorExpression)
            {
                if (failed)
                    _diagnostics.AddRange(diagnostics);
                else if (format is not null && TrialDiagnostics(() => Call("Hole", [value], span)).All(d => d.Severity != DiagnosticSeverity.Error))
                    _diagnostics.ReportInterpolationKindFormat(syntax.Parts[i].Span, prefix, alignment: false);
                else
                    _diagnostics.ReportNotInInterpolationKind(span, value.Type, prefix, KindHolds(clr));
                // Carry on, to report every hole that's wrong.
                holeErrors = true;
                continue;
            }
            _diagnostics.AddRange(diagnostics);
            builder = next;
        }
        if (failed || holeErrors)
            return new BoundErrorExpression();

        var built = Call("Build", [], syntax.Span);
        if (!failed && built.Type != kind)
        {
            _diagnostics.ReportInvalidInterpolationKind(syntax.Span, kind, $"Build() on its builder returning '{kind}'");
            return new BoundErrorExpression();
        }
        return built;
    }

    /// <summary>
    /// A plain <c>$"…"</c> given to a .NET parameter that takes a kind (<c>Sql</c>) or a <c>FormattableString</c>, as C#
    /// converts one. Null if it isn't such an argument.
    /// </summary>
    private BoundExpression? ConvertInterpolatedArgument(BoundExpression argument, TypeSymbol parameter)
    {
        if (argument is not BoundInterpolatedStringExpression { Syntax: { Prefix: null } } interpolated || parameter == TypeSymbol.String)
            return null;
        if (IsInterpolationKind(parameter))
            return BindAsKind(interpolated, parameter);
        if (IsFormattableString(parameter))
            return new BoundCastExpression(parameter, interpolated);
        return null;
    }

    /// <summary>
    /// How well a plain <c>$"…"</c> fits a parameter of a kind or <c>FormattableString</c>: less well than a string, as in
    /// C#, where <c>Log(string)</c> beats <c>Log(FormattableString)</c>.
    /// </summary>
    private static int? InterpolatedArgumentScore(BoundExpression argument, TypeSymbol parameter) =>
        argument is BoundInterpolatedStringExpression { Syntax: { Prefix: null } } && (IsInterpolationKind(parameter) || IsFormattableString(parameter))
            ? 5
            : null;

    /// <summary>
    /// EF Core's methods that take SQL as a string and parameters separately, and the method whose holes are
    /// parameters instead: an interpolated string given to one of these puts its holes in the SQL as text.
    /// </summary>
    private static readonly Dictionary<string, string> RawSqlMethods = new()
    {
        ["FromSqlRaw"] = "FromSql($\"…\"), or the data plugin's Query(sql$\"…\")",
        ["SqlQueryRaw"] = "SqlQuery($\"…\"), or the data plugin's Query(sql$\"…\")",
        ["ExecuteSqlRaw"] = "ExecuteSql($\"…\"), or the data plugin's Execute(sql$\"…\")",
        ["ExecuteSqlRawAsync"] = "ExecuteSqlAsync($\"…\"), or the data plugin's ExecuteAsync(sql$\"…\")",
    };

    /// <summary>
    /// Warns when an interpolated string with holes is given as SQL text: to EF Core's <c>…Raw</c> methods, or as a
    /// string where the same method has an overload taking a kind or a <c>FormattableString</c> there, which C# (and
    /// Piper) prefers the string overload to.
    /// </summary>
    private void CheckInterpolatedSql(MethodBase chosen, IEnumerable<MethodBase> members, ImmutableArray<BoundExpression> arguments, int[]? positions)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] is not BoundInterpolatedStringExpression { Syntax: { Prefix: null } syntax } interpolated ||
                !interpolated.Parts.Any(p => p is not BoundLiteralExpression { Value: string }))
                continue;
            int position = positions?[i] ?? i;
            if (RawSqlMethods.TryGetValue(chosen.Name, out var safe) && chosen.DeclaringType?.Namespace?.StartsWith("Microsoft.EntityFrameworkCore") == true)
            {
                _diagnostics.ReportInterpolatedRawSql(syntax.Span, chosen.Name, safe);
                continue;
            }
            var safer = members
                .Where(m => !ReferenceEquals(m, chosen))
                .Select(m => m.GetParameters() is var ps && position < ps.Length ? ps[position].ParameterType : null)
                .FirstOrDefault(t => t is not null && (t == typeof(FormattableString) || KindPrefix(t) is not null));
            if (safer is not null)
                _diagnostics.ReportStringOverloadChosen(syntax.Span, chosen.Name, safer.Name, KindPrefix(safer));
        }
    }
}
