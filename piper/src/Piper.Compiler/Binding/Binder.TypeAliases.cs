using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// Type aliases, <c>type Lookup = Dictionary&lt;string, List&lt;int&gt;&gt;;</c>: another name for a type, as C#'s
/// <c>using Lookup = …;</c>. The name is declared as the type itself, so it's interchangeable with it everywhere and
/// leaves nothing in the C#. Aliases are resolved once every type is declared, before anything names them, each after
/// the aliases its own type mentions.
/// </summary>
public sealed partial class Binder
{
    private void DeclareTypeAliases()
    {
        var aliases = _files
            .SelectMany(file => file.Unit.Declarations.OfType<TypeAliasDeclarationSyntax>().Where(a => !a.Name.IsMissing).Select(a => (File: file, Syntax: a)))
            .ToList();
        var resolving = new HashSet<TypeAliasDeclarationSyntax>();
        var resolved = new HashSet<TypeAliasDeclarationSyntax>();
        foreach (var alias in aliases)
            Resolve(alias.File, alias.Syntax);

        void Resolve(FileContext file, TypeAliasDeclarationSyntax syntax)
        {
            if (resolved.Contains(syntax))
                return;
            if (!resolving.Add(syntax))
            {
                InFile(file);
                _diagnostics.ReportTypeAliasCycle(syntax.Name.Span, syntax.Name.Text);
                DeclareAlias(file, syntax, TypeSymbol.Error);
                resolved.Add(syntax);
                return;
            }
            var mentioned = SyntaxWalker.DescendantsAndSelf(syntax.Target).OfType<NamedTypeSyntax>().Select(t => t.Name.Text).ToHashSet();
            foreach (var other in aliases.Where(a => mentioned.Contains(a.Syntax.Name.Text)))
                Resolve(other.File, other.Syntax);
            if (resolved.Contains(syntax))    // part of a cycle, already reported
                return;

            InFile(file);
            DeclareAlias(file, syntax, BindType(syntax.Target));
            resolved.Add(syntax);
        }
    }

    /// <summary>Declares <paramref name="syntax"/>'s name as <paramref name="type"/>, in its file if private or its namespace otherwise.</summary>
    private void DeclareAlias(FileContext file, TypeAliasDeclarationSyntax syntax, TypeSymbol type)
    {
        var name = syntax.Name.Text;
        CheckReservedName(syntax.Name);
        bool clash = file.Private.LookupLocal(name) is not null || file.Namespace.Members.LookupLocal(name) is not null;
        var scope = syntax.Private is not null ? file.Private : file.Namespace.Members;
        if (clash || !scope.TryDeclare(name, type))
        {
            _diagnostics.ReportDuplicateDeclaration(syntax.Name.Span, name);
            return;
        }
        if (type != TypeSymbol.Error)
            _semantic?.AddOccurrence(file.Source, syntax.Name.Span, type, isDeclaration: false);
    }
}
