using System.Reflection;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// <c>nameof(…)</c>: a name's last part as a constant string, once the name is checked, as in C#. Binding the name
/// records it in the semantic model, so renaming what it names renames it too.
/// </summary>
public sealed partial class Binder
{
    private BoundExpression BindNameOf(NameOfExpressionSyntax syntax)
    {
        var name = syntax.Operand switch
        {
            NameExpressionSyntax { Identifier: var identifier } => identifier,
            MemberAccessExpressionSyntax { Member: var member } => member,
            _ => null,
        };
        if (name is null)
        {
            _diagnostics.ReportNameOfNeedsName(syntax.Operand.Span);
            return new BoundErrorExpression();
        }
        if (!Names(syntax.Operand))
        {
            // `nameof(Point.z)`: any member will do, so say there's none, rather than (as binding would) no method.
            if (syntax.Operand is MemberAccessExpressionSyntax access && Trial(() => ResolveContainer(access.Target), out _) is TypeSymbol type && type != TypeSymbol.Error)
                _diagnostics.ReportUnknownMember(access.Member.Span, type, access.Member.Text);
            else
                BindExpression(syntax.Operand);    // bound for real, to report why the name isn't one
            return new BoundErrorExpression();
        }
        return new BoundLiteralExpression(name.Text, TypeSymbol.String);
    }

    /// <summary>
    /// Whether <paramref name="syntax"/> names something: a value (a variable, parameter, field or function), a type or
    /// namespace, or a member of a type, including an instance member named through the type (<c>Order.Total</c>), as
    /// C#'s <c>nameof</c> allows.
    /// </summary>
    private bool Names(ExpressionSyntax syntax)
    {
        Trial(() => BindExpression(syntax), out bool failed);
        if (!failed)
            return true;
        if (Trial(() => ResolveContainer(syntax), out _) is (NamespaceSymbol or TypeSymbol or ClrTypeGroupSymbol) and not ErrorSymbol)
            return true;
        return syntax is MemberAccessExpressionSyntax access &&
               Trial(() => ResolveContainer(access.Target), out _) is TypeSymbol type && type != TypeSymbol.Error &&
               HasMember(type, access.Member.Text);
    }

    private bool HasMember(TypeSymbol type, string name) =>
        FindField(type, name) is not null ||
        FindMethod(type, name) is not null ||
        type.OriginalDefinition is EnumSymbol @enum && @enum.Variants.Any(v => v.Name == name) ||
        (IsClrBacked(type) || type.OriginalDefinition is ClassSymbol) &&
            ClrContext(type).Type.GetMember(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Length > 0;
}
