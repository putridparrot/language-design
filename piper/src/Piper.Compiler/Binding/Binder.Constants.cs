using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// <c>const</c> declarations: at the top level, in classes and in blocks. A constant's value is worked out when it's
/// first used (by another constant, a default value, …) or, at the latest, once functions are declared, so constants
/// can be declared in any order and any file. Uses are bound as the value itself.
/// </summary>
public sealed partial class Binder
{
    /// <summary>The program's own diagnostics: what a constant's value reports belongs there even if a Trial is the first to use it.</summary>
    private readonly DiagnosticBag _programDiagnostics;

    /// <summary>Where each constant not yet worked out was declared.</summary>
    private readonly Dictionary<ConstantSymbol, (FileContext File, Scope Scope)> _pendingConstants = [];

    private readonly HashSet<ConstantSymbol> _evaluatingConstants = [];

    /// <summary>Declares the constants of every file and class, before anything that could name one is bound.</summary>
    private ImmutableArray<ConstantSymbol> DeclareConstants(IEnumerable<ClassSymbol> classes)
    {
        var constants = ImmutableArray.CreateBuilder<ConstantSymbol>();
        foreach (var file in _files)
        {
            InFile(file);
            foreach (var syntax in file.Unit.Declarations.OfType<ConstDeclarationSyntax>())
            {
                var constant = new ConstantSymbol(syntax) { DeclaringFile = file.Source };
                _pendingConstants.Add(constant, (file, file.Scope));
                if (syntax.Name.IsMissing)
                    continue;
                Declare(constant, syntax, file);
                constants.Add(constant);
            }
        }
        foreach (var @class in classes)
        {
            var file = FileOf(@class);
            InFile(file);
            var scope = new Scope(file.Scope);
            var own = ImmutableArray.CreateBuilder<ConstantSymbol>();
            foreach (var syntax in @class.Declaration.Constants)
            {
                var constant = new ConstantSymbol(syntax) { Owner = @class, Documentation = syntax.Documentation, DeclaringFile = file.Source, Namespace = file.Namespace };
                _pendingConstants.Add(constant, (file, scope));
                if (syntax.Name.IsMissing)
                    continue;
                Declared(constant, syntax.Name);
                if (constant.Name == @class.Name || ReservedMemberNames.Contains(constant.Name) || !scope.TryDeclare(constant))
                    ReportMemberClash(syntax.Name);
                else
                    own.Add(constant);
            }
            @class.Constants = own.ToImmutable();
        }
        return constants.ToImmutable();
    }

    private void BindConstants()
    {
        foreach (var constant in _pendingConstants.Keys.ToList())
            Evaluate(constant);
    }

    /// <summary>A scope in which a class's type parameters and constants are visible.</summary>
    private static Scope ClassScope(ClassSymbol @class, FileContext file)
    {
        var scope = TypeParameterScope(@class, file);
        foreach (var constant in @class.Constants)
            scope.TryDeclare(constant);
        return scope;
    }

    /// <summary><c>const Max = 5;</c> in a block: declared once its value is bound, as a variable is.</summary>
    private BoundStatement BindConstStatement(ConstStatementSyntax syntax)
    {
        var constant = new ConstantSymbol(syntax.Declaration) { DeclaringFile = _file.Source };
        EvaluateIn(constant);
        Declared(constant, syntax.Declaration.Name);
        if (!syntax.Declaration.Name.IsMissing && !_scope.TryDeclare(constant))
            _diagnostics.ReportDuplicateDeclaration(syntax.Declaration.Name.Span, constant.Name);
        return new BoundBlockStatement([]);
    }

    /// <summary>A use of a constant: its value, as an expression of its own.</summary>
    private BoundExpression ConstantValue(ConstantSymbol constant)
    {
        Evaluate(constant);
        return constant.Value switch
        {
            null => new BoundErrorExpression(),
            BoundLiteralExpression literal => new BoundLiteralExpression(literal.Value, literal.Type),
            var value => value,
        };
    }

    private void Evaluate(ConstantSymbol constant)
    {
        if (_evaluatingConstants.Contains(constant))
        {
            // Reported at that constant, in its own file.
            var (source, name) = (_programDiagnostics.CurrentSource, constant.Declaration.Name);
            _programDiagnostics.CurrentSource = constant.DeclaringFile!;
            _programDiagnostics.ReportConstantCycle(name.Span, name.Text);
            _programDiagnostics.CurrentSource = source;
            return;
        }
        if (!_pendingConstants.TryGetValue(constant, out var declared))
            return;

        var (file, scope, diagnostics, @class, trials) = (_file, _scope, _diagnostics, _currentClass, _trials);
        var current = _programDiagnostics.CurrentSource;
        _evaluatingConstants.Add(constant);
        try
        {
            _diagnostics = _programDiagnostics;
            _trials = 0;
            InFile(declared.File);
            _scope = declared.Scope;
            _currentClass = constant.Owner;
            EvaluateIn(constant);
        }
        finally
        {
            _evaluatingConstants.Remove(constant);
            _pendingConstants.Remove(constant);
            _programDiagnostics.CurrentSource = current;
            (_file, _scope, _diagnostics, _currentClass, _trials) = (file, scope, diagnostics, @class, trials);
        }
    }

    /// <summary>Works out a constant's type and value in the current scope.</summary>
    private void EvaluateIn(ConstantSymbol constant)
    {
        var syntax = constant.Declaration;
        var declaredType = syntax.Type is null ? null : BindValueType(syntax.Type, "constant");
        if (declaredType is not null)
            constant.Type = declaredType;
        // Said before it's bound, as a call in it may be to a function that isn't declared yet.
        if (NotConstant(syntax.Value) is { } culprit)
        {
            _diagnostics.ReportNotConstant(culprit.Span, NotConstantReason);
            return;
        }
        int reported = _diagnostics.Count;
        var bound = RequireValue(BindExpression(syntax.Value, declaredType), syntax.Value.Span);
        if (declaredType is not null)
            bound = Convert(bound, declaredType, syntax.Value.Span);
        var type = declaredType ?? bound.Type;
        constant.Type = type;
        if (bound.Type == TypeSymbol.Error || type == TypeSymbol.Error)
            return;
        bool isEnum = type is ClrTypeSymbol { Type.IsEnum: true };
        if (!(type.IsNumeric || type == TypeSymbol.Bool || type == TypeSymbol.Char || type == TypeSymbol.String || IsDuration(type) || isEnum))
        {
            _diagnostics.ReportNotConstant(syntax.Type?.Span ?? syntax.Value.Span,
                $"A constant can't be a '{type}': only a number, bool, char, string, duration or .NET enum.");
            return;
        }
        if (isEnum)
            constant.Value = IsAttributeConstant(bound) ? bound : null;
        else if (ConstantFolding.Constant(bound) is { } value)
            constant.Value = new BoundLiteralExpression(value, type);
        // Overflow or division by zero has been said.
        if (constant.Value is null && _diagnostics.Count == reported)
            _diagnostics.ReportNotConstant(syntax.Value.Span, NotConstantReason);
    }

    private const string NotConstantReason =
        "A constant's value must be known when compiling: a literal, another constant, a .NET constant or enum member, or an operator on those. Use 'static let' in a class, or a function, for one worked out as the program runs.";

    /// <summary>The part of <paramref name="syntax"/> that couldn't be constant whatever it means: a call, a lambda, ….</summary>
    private static ExpressionSyntax? NotConstant(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax or NameExpressionSyntax or NameOfExpressionSyntax or ErrorExpressionSyntax => null,
        MemberAccessExpressionSyntax member => NotConstant(member.Target),
        ParenthesizedExpressionSyntax parenthesized => NotConstant(parenthesized.Expression),
        UnaryExpressionSyntax unary => NotConstant(unary.Operand),
        BinaryExpressionSyntax binary => NotConstant(binary.Left) ?? NotConstant(binary.Right),
        CastExpressionSyntax cast => NotConstant(cast.Operand),
        InterpolatedStringExpressionSyntax { Prefix: null } interpolated => interpolated.Parts.OfType<InterpolationSyntax>()
            .Select(hole => hole.Alignment is null && hole.Format is null ? NotConstant(hole.Expression) : hole.Expression).FirstOrDefault(e => e is not null),
        _ => syntax,
    };

    /// <summary>A class's constant (or one of its Piper base classes').</summary>
    private static ConstantSymbol? FindConstant(TypeSymbol type, string name) =>
        (type.OriginalDefinition as ClassSymbol)?.SelfAndBases().SelectMany(c => c.Constants).FirstOrDefault(c => c.Name == name);

    /// <summary>Records a use of a class's constant, which must be accessible here.</summary>
    private void UseClassConstant(ConstantSymbol constant, Token name)
    {
        Record(name, constant);
        if (!constant.IsPublic)
            CheckMemberAccessible(constant.Name, constant.Owner!, constant.IsProtected, receiver: null, name.Span);
    }

    /// <summary>The constant a pattern names (<c>Max</c>, <c>Limits.Max</c>), if it names one.</summary>
    private ConstantSymbol? PatternConstant(PatternSyntax syntax)
    {
        switch (syntax)
        {
            case IdentifierPatternSyntax { IsBinding: false, Identifier: { IsMissing: false } name }:
                return _scope.Lookup(name.Text) as ConstantSymbol;
            case ConstructorPatternSyntax { HasArgumentList: false, Qualifier: { IsMissing: false } qualifier, Name: { IsMissing: false } name }:
                return _scope.Lookup(qualifier.Text) switch
                {
                    NamespaceSymbol @namespace => MemberOf(@namespace, name.Text) as ConstantSymbol,
                    ClassSymbol @class => FindConstant(@class, name.Text),
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>Records the names of a pattern that is <paramref name="constant"/>, which must be accessible here.</summary>
    private void UsePatternConstant(PatternSyntax syntax, ConstantSymbol constant)
    {
        if (syntax is IdentifierPatternSyntax { Identifier: var bare })
        {
            Record(bare, constant);
            return;
        }
        var qualified = (ConstructorPatternSyntax)syntax;
        Record(qualified.Qualifier!, _scope.Lookup(qualified.Qualifier!.Text));
        if (constant.Owner is null)
            Record(qualified.Name, constant);
        else
            UseClassConstant(constant, qualified.Name);
    }
}
