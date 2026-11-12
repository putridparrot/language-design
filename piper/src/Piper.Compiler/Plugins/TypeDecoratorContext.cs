using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins;

/// <summary>
/// What a <see cref="Decorator"/>'s <see cref="Decorator.Type"/> gets: the class or record it's on, and the attribute
/// that named it. It returns the type rewritten, often with <see cref="WithMembers"/> and <see cref="WithBase"/>, and
/// any declarations to add beside it. All of it is ordinary Piper syntax, checked as if the user had written it, so
/// this is code generation at compile time, as a C# source generator is, with nothing happening at run time.
/// </summary>
public sealed class TypeDecoratorContext
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<string, Token> _freshName;

    internal TypeDecoratorContext(IPiperPlugin plugin, AttributeSyntax attribute, DeclarationSyntax declaration,
        DiagnosticBag diagnostics, Func<string, Token> freshName)
    {
        Plugin = plugin;
        Attribute = attribute;
        Declaration = declaration;
        _diagnostics = diagnostics;
        _freshName = freshName;
        Template = new SyntaxTemplate(attribute.Span);
    }

    public IPiperPlugin Plugin { get; }

    /// <summary>The attribute naming the decorator, <c>[Notify]</c>; it isn't passed on to C#.</summary>
    public AttributeSyntax Attribute { get; }

    /// <summary>The attribute's arguments, as written.</summary>
    public ImmutableArray<ExpressionSyntax> Arguments => Attribute.Arguments;

    /// <summary>
    /// The class or record, as decorators nearer to it have left it, without the decorator attributes, and with its
    /// methods' own decorators applied. Rewrite it with <c>with</c>, so what the decorator doesn't change stays.
    /// </summary>
    public DeclarationSyntax Declaration { get; }

    /// <summary>The declaration, if it's a class.</summary>
    public ClassDeclarationSyntax? Class => Declaration as ClassDeclarationSyntax;

    /// <summary>The declaration, if it's a record (or record struct). Its methods go in an <c>extend</c> block beside it.</summary>
    public RecordDeclarationSyntax? Record => Declaration as RecordDeclarationSyntax;

    /// <summary>The type's name.</summary>
    public string Name => Declaration.Name.Text;

    /// <summary>Builds Piper syntax from source with holes (see <see cref="SyntaxTemplate"/>), placed at the attribute.</summary>
    public SyntaxTemplate Template { get; }

    /// <summary>A name for a member the decorator adds, such as <c>__name_1</c>, which can't clash with the user's.</summary>
    public Token FreshName(string hint) => _freshName(hint);

    /// <summary>
    /// The marker <paramref name="name"/> among a member's <paramref name="attributes"/> (<c>[Command]</c> on a method),
    /// if it's there; see <see cref="Decorator.ReadBy"/>. Markers are removed after the type decorator runs.
    /// </summary>
    public static AttributeSyntax? Marker(ImmutableArray<AttributeSyntax> attributes, string name) =>
        attributes.FirstOrDefault(a => a.Name.Parts is [var part] && part.Text == name);

    /// <summary>Reports an error; the message is followed by the plugin's name.</summary>
    public void Report(TextSpan span, string message) => _diagnostics.ReportPluginError(span, Plugin.Name, message);

    /// <summary>
    /// The members of a class written in Piper, with holes: <c>ClassMembers("mut $f: int = 0; fn Get() => int = $f;", ("f", name))</c>.
    /// Write them as in a class's body.
    /// </summary>
    public (ImmutableArray<ClassFieldSyntax> Fields, ImmutableArray<FunctionDeclarationSyntax> Methods, ImmutableArray<ClassEventSyntax> Events)
        ClassMembers(string template, params (string Name, object Value)[] holes)
    {
        var @class = (ClassDeclarationSyntax)Template.Declarations($"class __Template {{\n{template}\n}}", holes).Single();
        return (@class.Fields, @class.Methods, @class.Events);
    }

    /// <summary>The class with <paramref name="fields"/>, <paramref name="methods"/> and <paramref name="events"/> added after its own.</summary>
    public ClassDeclarationSyntax WithMembers(
        IEnumerable<ClassFieldSyntax>? fields = null, IEnumerable<FunctionDeclarationSyntax>? methods = null, IEnumerable<ClassEventSyntax>? events = null)
    {
        var @class = RequireClass();
        return @class with
        {
            Fields = @class.Fields.AddRange(fields ?? []),
            Methods = @class.Methods.AddRange(methods ?? []),
            Events = @class.Events.AddRange(events ?? []),
        };
    }

    /// <summary>The class with <paramref name="type"/> (a trait or interface) added to its base list.</summary>
    public ClassDeclarationSyntax WithBase(TypeSyntax type)
    {
        var @class = RequireClass();
        return @class with { Bases = @class.Bases.Add(new BaseTypeSyntax(type.Span, type, null)) };
    }

    /// <summary>
    /// The class with <paramref name="decorate"/>, a function decorator, applied to each of its methods that has a body
    /// (not its property accessors or constructors), as if each had the attribute.
    /// </summary>
    public ClassDeclarationSyntax DecorateMethods(Func<DecoratorContext, FunctionDeclarationSyntax> decorate)
    {
        var @class = RequireClass();
        return @class with
        {
            Methods = [.. @class.Methods.Select(method => method.HasBody && method.AccessorOf is null && method.Constructor is null
                ? decorate(new DecoratorContext(Plugin, Attribute, method, isMethod: true, _diagnostics, _freshName))
                : method)],
        };
    }

    private ClassDeclarationSyntax RequireClass() =>
        Class ?? throw new InvalidOperationException($"'{Name}' isn't a class; check Class before changing its members.");
}
