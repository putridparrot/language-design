using System.Reflection;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// Newtypes, <c>newtype IssueId = int;</c>: a record struct with one field, <c>value</c>, that can't be mixed up with
/// another over the same type. The parser makes the record; here it's given <c>Parse</c>, as an enum is, so it can be
/// read from text as its value can. (The emitter adds the rest: printing, sorting, formatting and JSON as its value,
/// and the <c>TryParse</c> ASP.NET Core binds route and query values with, which calls <c>Parse</c>.)
/// </summary>
public sealed partial class Binder
{
    /// <summary>
    /// <c>static fn Parse(text: string) => IssueId?</c> for each newtype whose value can be read from text (a string, a
    /// number, <c>bool</c>, <c>char</c>, or a .NET type with a static <c>TryParse</c>), unless it declares its own. It's
    /// None for text that isn't a value, or whose value the newtype's invariants reject. Numbers are read in the
    /// invariant culture, as ASP.NET Core reads route and query values.
    /// </summary>
    private IEnumerable<FunctionInfo> BindNewtypeParsers(IEnumerable<TypeSymbol> types)
    {
        var methods = new List<FunctionInfo>();
        foreach (var record in types.OfType<RecordSymbol>().Where(r => r.IsNewtype && r.Fields.Length == 1))
        {
            if (record.Methods.Any(m => m.Name == "Parse") || ParseText(record.Fields[0].Type) is not { } parse)
                continue;
            var name = record.Name;
            var make = record.Declaration.Requires.IsEmpty
                ? $"return Some({name}(parsed));"
                : $"try {{\nreturn Some({name}(parsed));\n}} catch (_: System.ArgumentException) {{\nreturn None;\n}}";
            var file = FileOf(record);
            InFile(file);
            // Placed at the newtype's name, so an error in it points there.
            var syntax = (ExtendDeclarationSyntax)new SyntaxTemplate(record.Declaration.Name.Span)
                .Declarations($"extend {name} {{\nstatic fn Parse(text: string) => {name}? {{\n{parse}\n{make}\n}}\n}}")
                .Single();
            methods.AddRange(BindExtendMethods(new ExtendInfo(syntax, file, record, TypeParameterScope(record, file), [])));
        }
        return methods;
    }

    /// <summary>Piper reading <c>text</c> into a variable <c>parsed</c> of <paramref name="type"/>, returning None if it can't; null if it can't be read from text.</summary>
    private static string? ParseText(TypeSymbol type)
    {
        if (type == TypeSymbol.String)
            return "let parsed = text;";
        if (type == TypeSymbol.Bool || type == TypeSymbol.Char)
            return $"if !{type.Name}.TryParse(text, out let parsed) {{\nreturn None;\n}}";
        if (type.IsNumeric)
            return $"if !{type.Name}.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out let parsed) {{\nreturn None;\n}}";
        if (type is not ClrTypeSymbol { IsArray: false } clr || clr.Type is { IsNested: true } or { IsGenericType: true } || clr.Type.FullName is not { } fullName)
            return null;

        var signatures = clr.Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "TryParse" && m.ReturnType.FullName == "System.Boolean")
            .Select(m => m.GetParameters().Select(p => p.ParameterType.FullName).ToList())
            .ToList();
        bool Has(params string[] parameters) => signatures.Any(s => s.SequenceEqual(parameters));
        if (Has("System.String", "System.IFormatProvider", fullName + "&"))
            return $"if !{fullName}.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out let parsed) {{\nreturn None;\n}}";
        if (Has("System.String", fullName + "&"))
            return $"if !{fullName}.TryParse(text, out let parsed) {{\nreturn None;\n}}";
        return null;
    }
}
