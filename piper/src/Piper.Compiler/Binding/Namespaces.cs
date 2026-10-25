namespace Piper.Binding;

/// <summary>
/// A namespace: Piper declarations from any number of files, child namespaces, and (looked up on demand)
/// the .NET types and namespaces of the same name. The global namespace has an empty <see cref="FullName"/>.
/// </summary>
public sealed class NamespaceSymbol : Symbol
{
    private readonly Dictionary<string, NamespaceSymbol> _children = [];

    /// <param name="clr">The program's .NET types; a child namespace has its parent's, and the global one the platform's by default.</param>
    public NamespaceSymbol(string name, NamespaceSymbol? parent, Scope members, ClrTypeIndex? clr = null) : base(name)
    {
        Parent = parent;
        Members = members;
        FullName = parent is null || parent.IsGlobal ? name : $"{parent.FullName}.{name}";
        Clr = clr ?? parent?.Clr ?? ClrTypeIndex.Platform;
    }

    public NamespaceSymbol? Parent { get; }

    /// <summary>The .NET types the program can use: the platform's, and those of the packages and assemblies it references.</summary>
    public ClrTypeIndex Clr { get; }

    public string FullName { get; }

    public bool IsGlobal => Parent is null;

    /// <summary>The public Piper declarations in this namespace (private ones live in their file's scope).</summary>
    public Scope Members { get; }

    /// <summary>True if any Piper file declares this namespace (or one inside it).</summary>
    public bool IsDeclaredInPiper { get; internal set; }

    public NamespaceSymbol GetOrAddChild(string name, Func<Scope> createMembers)
    {
        if (!_children.TryGetValue(name, out var child))
            _children[name] = child = new NamespaceSymbol(name, this, createMembers());
        return child;
    }

    /// <summary>
    /// A member: a Piper declaration, a child namespace (Piper or .NET), or .NET types with this name.
    /// .NET types are returned as a <see cref="TypeSymbol"/> (or a <see cref="ClrTypeGroupSymbol"/> when
    /// several generic arities share the name).
    /// </summary>
    public Symbol? LookupMember(string name, EnumSymbol option, Func<Scope> createMembers)
    {
        if (Members.LookupLocal(name) is { } member)
            return member;
        if (_children.TryGetValue(name, out var child))
            return child;

        var childName = IsGlobal ? name : $"{FullName}.{name}";
        if (Clr.IsNamespace(childName))
            return GetOrAddChild(name, createMembers);

        if (IsGlobal)
            return null;
        var types = Clr.FindTypes(FullName, name);
        return types.Count switch
        {
            0 => null,
            1 => ClrTypes.FromClr(types[0], new Dictionary<Type, TypeSymbol>(), option) ?? ClrTypeSymbol.For(types[0]),
            _ => new ClrTypeGroupSymbol(name, types),
        };
    }

    public override string ToString() => IsGlobal ? "<global>" : FullName;
}
