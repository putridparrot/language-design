using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// Structured logging from interpolation. <c>log.LogInformation($"Reserved {quantity} of {sku}")</c> logs a template
/// with named fields, as C#'s <c>LoggerMessage</c> generator would: <c>LogInformation("Reserved {quantity} of {sku}",
/// quantity, sku)</c>. So the log is natural Piper, while a log store (Aspire's dashboard, Seq, Application Insights)
/// gets <c>quantity</c> and <c>sku</c> as fields to query, and every line of one call shares its template. In C#,
/// passing <c>$"…"</c> to these methods loses the fields (CA2254 warns about it); here it's the way to write them.
/// As a statement, the call checks the level first (<c>if (log.IsEnabled(LogLevel.Information))</c>), so a disabled
/// level costs nothing: the holes aren't evaluated, as C#'s interpolated string handlers skip them.
/// </summary>
public sealed partial class Binder
{
    private const string LoggerExtensions = "Microsoft.Extensions.Logging.LoggerExtensions";

    private static readonly HashSet<string> LogLevels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    /// <summary>
    /// The arguments of a call to one of <c>LoggerExtensions</c>' <c>Log…</c> methods with its message given as a plain
    /// <c>$"…"</c> (and no arguments of its own): the message as a template, each hole a named field, and the holes'
    /// values after it. And the level it logs at, unless that's an argument (<c>Log(level, …)</c>). Anything else is
    /// left as it is.
    /// </summary>
    private static (ImmutableArray<BoundExpression> Arguments, string? Level) LoggingTemplate(
        MethodBase member, ImmutableArray<BoundExpression> arguments, ImmutableArray<string?> names)
    {
        if (member is not MethodInfo { Name: var name } method || method.DeclaringType?.FullName != LoggerExtensions ||
            !name.StartsWith("Log", StringComparison.Ordinal) || !names.IsEmpty)
            return (arguments, null);
        int message = Array.FindIndex(method.GetParameters(), p => p.Name == "message");
        if (message < 0 || message != arguments.Length - 1 ||
            arguments[message] is not BoundInterpolatedStringExpression { Syntax: { Prefix: null } syntax } interpolated ||
            interpolated.Parts.All(p => p is BoundLiteralExpression { Value: string }))
            return (arguments, null);

        var template = new StringBuilder();
        var values = new List<BoundExpression>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (part, written) in interpolated.Parts.Zip(syntax.Parts))
        {
            if (part is BoundLiteralExpression { Value: string text } && written is InterpolatedTextSyntax)
            {
                // The template's own braces are its fields', so the text's are doubled.
                template.Append(text.Replace("{", "{{").Replace("}", "}}"));
                continue;
            }
            var (value, alignment, format) = part is BoundFormattedExpression formatted
                ? (formatted.Value, formatted.Alignment, formatted.Format)
                : (part, null, null);
            var field = FieldName(((InterpolationSyntax)written).Expression);
            for (int n = 2; !taken.Add(field); n++)
                field = $"{FieldName(((InterpolationSyntax)written).Expression)}{n}";
            template.Append('{').Append(field);
            if (alignment is { } width)
                template.Append(',').Append(width);
            if (format is not null)
                template.Append(':').Append(format);
            template.Append('}');
            values.Add(value);
        }

        var level = name["Log".Length..];
        return ([.. arguments.SetItem(message, new BoundLiteralExpression(template.ToString(), TypeSymbol.String)), .. values],
            LogLevels.Contains(level) ? level : null);
    }

    /// <summary>
    /// A hole's field name, from what it says: <c>{quantity}</c> is <c>quantity</c>, <c>{order.id}</c> is
    /// <c>order.id</c>, <c>{Total(order)}</c> is <c>Total</c>; anything else is <c>value</c>.
    /// </summary>
    private static string FieldName(ExpressionSyntax hole) => hole switch
    {
        NameExpressionSyntax name => name.Identifier.Text,
        MemberAccessExpressionSyntax { Target: var target, Member.Text: var member } when FieldName(target) is var owner && owner != "value" =>
            $"{owner}.{member}",
        MemberAccessExpressionSyntax { Member.Text: var member } => member,
        CallExpressionSyntax { Callee: var callee } => FieldName(callee),
        _ => "value",
    };
}
