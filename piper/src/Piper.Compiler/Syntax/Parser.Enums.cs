using System.Collections.Immutable;
using Piper.Plugins;

namespace Piper.Syntax;

/// <summary>
/// What every enum has without writing it, added as an ordinary <c>extend</c> block, so it's checked and compiled as
/// the user's code is:
/// <list type="bullet">
/// <item><c>value.Name</c>: the variant's name, <c>"Circle"</c> for <c>Shape.Circle(2.0)</c>.</item>
/// <item>For an enum whose variants have no fields: <c>E.Values</c>, every variant in order, and <c>E.Parse(text)</c>,
/// the variant named, or None. So a status can come from a query string, a file or the command line.</item>
/// </list>
/// A member the enum's own extend blocks in the file declare, or a variant of the same name, is left to them.
/// </summary>
public sealed partial class Parser
{
    private static ImmutableArray<DeclarationSyntax> EnumMembers(ImmutableArray<DeclarationSyntax> declarations)
    {
        var added = ImmutableArray.CreateBuilder<DeclarationSyntax>();
        foreach (var @enum in declarations.OfType<EnumDeclarationSyntax>())
        {
            // An enum that's an error already (a variant named as the enum or a .NET object member, an enum named as a
            // builtin type) gets nothing, so the generated code doesn't add errors of its own.
            if (@enum.Variants.IsEmpty || @enum.Name.IsMissing || @enum.Variants.Any(v => v.Name.IsMissing) ||
                @enum.Name.Text is "Option" or "Result" ||
                @enum.Variants.Any(v => v.Name.Text == @enum.Name.Text || v.Name.Text is "ToString" or "Equals" or "GetHashCode" or "GetType"))
            {
                continue;
            }

            var taken = declarations.OfType<ExtendDeclarationSyntax>()
                .Where(e => e.Target is NamedTypeSyntax { Qualifier.IsEmpty: true } target && target.Name.Text == @enum.Name.Text)
                .SelectMany(e => e.Methods.Select(m => m.AccessorOf ?? m.Name.Text))
                .Concat(@enum.Variants.Select(v => v.Name.Text))
                .ToHashSet();

            var name = @enum.Name.Text;
            var members = new List<string>();
            if (!taken.Contains("Name"))
            {
                var arms = @enum.Variants.Select(v => v.Fields.IsEmpty
                    ? $"{name}.{v.Name.Text} => \"{v.Name.Text}\","
                    : $"{name}.{v.Name.Text}({string.Join(", ", v.Fields.Select(_ => "_"))}) => \"{v.Name.Text}\",");
                // JSON leaves it out: a variant is written as its kind and its fields (Piper.Runtime's PiperEnumJson).
                members.Add($"prop Name => string = match this {{ {string.Join(" ", arms)} }};");
            }
            if (@enum.TypeParameters.IsEmpty && @enum.Variants.All(v => v.Fields.IsEmpty))
            {
                if (!taken.Contains("Values"))
                    members.Add($"static prop Values => System.Collections.Generic.List<{name}> = [{string.Join(", ", @enum.Variants.Select(v => $"{name}.{v.Name.Text}"))}];");
                if (!taken.Contains("Parse"))
                {
                    var arms = @enum.Variants.Select(v => $"\"{v.Name.Text}\" => Some({name}.{v.Name.Text}),");
                    members.Add($"static fn Parse(name: string) => {name}? = match name {{ {string.Join(" ", arms)} _ => None }};");
                }
            }
            if (members.Count == 0)
                continue;

            // Placed at the enum's name, so an error in them (a clash with a .NET member, say) points there.
            var template = new SyntaxTemplate(@enum.Name.Span);
            added.AddRange(template.Declarations($"extend {name} {{\n{string.Join("\n", members)}\n}}"));
        }
        return added.ToImmutable();
    }
}
