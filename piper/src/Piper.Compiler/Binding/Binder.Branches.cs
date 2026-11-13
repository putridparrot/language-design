using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// The type of branches that build a Result or an Option between them, when nothing says what's expected:
/// <c>match r { Ok(v) =&gt; Ok(f(v)), Err(e) =&gt; Err(e) }</c>. Each of <c>Ok(…)</c> and <c>Err(…)</c> knows only half of
/// <c>Result&lt;T, E&gt;</c>, so on its own neither can be bound; together they say it.
/// </summary>
public sealed partial class Binder
{
    /// <summary>The variant a branch builds, as its value: <c>Ok(x)</c>, <c>Err(e)</c>, <c>Some(x)</c> or <c>None</c>.</summary>
    private static (string Variant, ExpressionSyntax? Argument)? BuiltVariant(ExpressionSyntax body)
    {
        while (body is BlockExpressionSyntax { Trailing: { } trailing })
            body = trailing;
        if (body is CallExpressionSyntax { Callee: var callee, Arguments: [var argument] } && VariantName(callee) is { } name &&
            name is "Ok" or "Err" or "Some")
        {
            return (name, argument);
        }
        return VariantName(body) == "None" ? ("None", null) : null;
    }

    /// <summary><c>Ok</c>, or <c>Result.Ok</c>: the variant's name, if the syntax names one of Option's or Result's.</summary>
    private static string? VariantName(ExpressionSyntax syntax) => syntax switch
    {
        NameExpressionSyntax { Identifier.Text: var name, TypeArguments.IsEmpty: true } => name,
        MemberAccessExpressionSyntax { Target: NameExpressionSyntax { Identifier.Text: "Result" or "Option" }, Member.Text: var name } => name,
        _ => null,
    };

    /// <summary>Whether any of <paramref name="bodies"/> builds a Result or Option variant, so the branches may say their type between them.</summary>
    private static bool AnyBuildsVariant(IEnumerable<ExpressionSyntax> bodies) => bodies.Any(b => BuiltVariant(b) is not null);

    /// <summary>
    /// Works out the type of branches from what each is on its own, where nothing is expected: a branch whose type is
    /// known (a Result or Option) gives it whole; one building <c>Ok(x)</c> gives T, <c>Err(e)</c> E, and <c>Some(x)</c> an
    /// Option's T. Null if they don't say enough. <paramref name="tryBranch"/> binds a branch's value (or, for a variant,
    /// its argument) as a trial, in the branch's own scope, returning null if that fails.
    /// </summary>
    private TypeSymbol? BranchesType(IReadOnlyList<ExpressionSyntax> bodies, Func<int, ExpressionSyntax, TypeSymbol?> tryBranch)
    {
        TypeSymbol? ok = null, error = null, some = null;
        for (int i = 0; i < bodies.Count; i++)
        {
            // A branch that binds on its own says the type outright (one that never completes says nothing).
            if (tryBranch(i, bodies[i]) is { } whole && whole != TypeSymbol.Never && whole != TypeSymbol.Error)
            {
                if (whole.OriginalDefinition == _result && whole.TypeArguments is [var t, var e])
                    (ok, error) = (ok ?? t, error ?? e);
                else if (whole.OriginalDefinition == _option && whole.TypeArguments is [var s])
                    some ??= s;
                else
                    return null;
                continue;
            }
            switch (BuiltVariant(bodies[i]))
            {
                case ("Ok", { } argument):
                    ok ??= tryBranch(i, argument);
                    break;
                case ("Err", { } argument):
                    error ??= tryBranch(i, argument);
                    break;
                case ("Some", { } argument):
                    some ??= tryBranch(i, argument);
                    break;
            }
        }

        if (ok is not null && error is not null && some is null)
            return _result.Construct([ok, error]);
        if (some is not null && ok is null && error is null)
            return _option.Construct([some]);
        return null;
    }
}
