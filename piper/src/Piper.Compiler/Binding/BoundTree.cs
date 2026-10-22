using System.Collections.Immutable;

namespace Piper.Binding;

// The bound tree is the type-checked form of the program: names are resolved to symbols and
// every expression has a type. The binder produces it; the lowerer rewrites it into a subset the
// C# emitter can translate directly (no block or if expressions in value positions).

public abstract class BoundNode;

public sealed class BoundProgram(
    ImmutableArray<TypeSymbol> types,
    ImmutableArray<BoundFunction> functions,
    FunctionSymbol? main)
{
    /// <summary>User-declared records and enums, in declaration order.</summary>
    public ImmutableArray<TypeSymbol> Types { get; } = types;

    public ImmutableArray<BoundFunction> Functions { get; } = functions;

    public FunctionSymbol? Main { get; } = main;
}

/// <summary>Before lowering <see cref="Body"/> is a <see cref="BoundBlockExpression"/>; after, a <see cref="BoundBlockStatement"/>.</summary>
public sealed class BoundFunction(FunctionSymbol symbol, BoundNode body, bool isExpressionBodied)
{
    public FunctionSymbol Symbol { get; } = symbol;

    public BoundNode Body { get; } = body;

    public bool IsExpressionBodied { get; } = isExpressionBodied;
}

// Statements

public abstract class BoundStatement : BoundNode;

public sealed class BoundBlockStatement(ImmutableArray<BoundStatement> statements) : BoundStatement
{
    public ImmutableArray<BoundStatement> Statements { get; } = statements;
}

/// <summary><see cref="Initializer"/> is null only for declarations introduced by lowering (assigned on every path afterwards).</summary>
public sealed class BoundVariableDeclaration(VariableSymbol variable, BoundExpression? initializer) : BoundStatement
{
    public VariableSymbol Variable { get; } = variable;

    public BoundExpression? Initializer { get; } = initializer;
}

public sealed class BoundExpressionStatement(BoundExpression expression) : BoundStatement
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundReturnStatement(BoundExpression? value) : BoundStatement
{
    public BoundExpression? Value { get; } = value;
}

public sealed class BoundIfStatement(BoundExpression condition, BoundStatement then, BoundStatement? @else) : BoundStatement
{
    public BoundExpression Condition { get; } = condition;

    public BoundStatement Then { get; } = then;

    public BoundStatement? Else { get; } = @else;
}

public sealed class BoundWhileStatement(BoundExpression condition, BoundStatement body) : BoundStatement
{
    public BoundExpression Condition { get; } = condition;

    public BoundStatement Body { get; } = body;
}

/// <summary>A counted loop over <c>Lower..Upper</c> (or <c>..=</c> when <see cref="IsInclusive"/>).</summary>
public sealed class BoundForStatement(
    VariableSymbol variable,
    BoundExpression lower,
    BoundExpression upper,
    bool isInclusive,
    BoundStatement body) : BoundStatement
{
    public VariableSymbol Variable { get; } = variable;

    public BoundExpression Lower { get; } = lower;

    public BoundExpression Upper { get; } = upper;

    public bool IsInclusive { get; } = isInclusive;

    public BoundStatement Body { get; } = body;
}

/// <summary>Produced only by lowering: the impossible fall-through at the end of an exhaustive match.</summary>
public sealed class BoundUnreachableStatement : BoundStatement;

public sealed class BoundBreakStatement : BoundStatement;

public sealed class BoundContinueStatement : BoundStatement;

// Expressions

public abstract class BoundExpression : BoundNode
{
    public abstract TypeSymbol Type { get; }
}

public sealed class BoundErrorExpression : BoundExpression
{
    public override TypeSymbol Type => TypeSymbol.Error;
}

public sealed class BoundLiteralExpression(object value, TypeSymbol type) : BoundExpression
{
    public object Value { get; } = value;

    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundVariableExpression(VariableSymbol variable) : BoundExpression
{
    public VariableSymbol Variable { get; } = variable;

    public override TypeSymbol Type => Variable.Type;
}

/// <summary><see cref="CompoundOperator"/> is set for <c>+=</c> and friends.</summary>
public sealed class BoundAssignmentExpression(VariableSymbol variable, BoundBinaryOperatorKind? compoundOperator, BoundExpression value)
    : BoundExpression
{
    public VariableSymbol Variable { get; } = variable;

    public BoundBinaryOperatorKind? CompoundOperator { get; } = compoundOperator;

    public BoundExpression Value { get; } = value;

    public override TypeSymbol Type => TypeSymbol.Unit;
}

public sealed class BoundUnaryExpression(BoundUnaryOperatorKind op, BoundExpression operand, TypeSymbol type) : BoundExpression
{
    public BoundUnaryOperatorKind Operator { get; } = op;

    public BoundExpression Operand { get; } = operand;

    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundBinaryExpression(BoundExpression left, BoundBinaryOperatorKind op, BoundExpression right, TypeSymbol type)
    : BoundExpression
{
    public BoundExpression Left { get; } = left;

    public BoundBinaryOperatorKind Operator { get; } = op;

    public BoundExpression Right { get; } = right;

    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundCallExpression(FunctionSymbol function, ImmutableArray<BoundExpression> arguments) : BoundExpression
{
    public FunctionSymbol Function { get; } = function;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type => Function.ReturnType;
}

public sealed class BoundRecordCreationExpression(RecordSymbol record, ImmutableArray<BoundExpression> arguments) : BoundExpression
{
    public RecordSymbol Record { get; } = record;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type => Record;
}

public sealed class BoundVariantCreationExpression(VariantSymbol variant, ImmutableArray<BoundExpression> arguments) : BoundExpression
{
    public VariantSymbol Variant { get; } = variant;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type => Variant.Enum;
}

public sealed class BoundMatchExpression(BoundExpression scrutinee, ImmutableArray<BoundMatchArm> arms, TypeSymbol type)
    : BoundExpression
{
    public BoundExpression Scrutinee { get; } = scrutinee;

    public ImmutableArray<BoundMatchArm> Arms { get; } = arms;

    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundMatchArm(BoundPattern pattern, BoundExpression? guard, BoundExpression body)
{
    public BoundPattern Pattern { get; } = pattern;

    public BoundExpression? Guard { get; } = guard;

    public BoundExpression Body { get; } = body;
}

/// <summary>Produced only by lowering: <c>expression is pattern</c>, which also binds the pattern's variables.</summary>
public sealed class BoundIsPatternExpression(BoundExpression expression, BoundPattern pattern) : BoundExpression
{
    public BoundExpression Expression { get; } = expression;

    public BoundPattern Pattern { get; } = pattern;

    public override TypeSymbol Type => TypeSymbol.Bool;
}

public sealed class BoundFieldAccessExpression(BoundExpression target, FieldSymbol field) : BoundExpression
{
    public BoundExpression Target { get; } = target;

    public FieldSymbol Field { get; } = field;

    public override TypeSymbol Type => Field.Type;
}

/// <summary>Text segments are represented as String literals; every other part is an interpolation hole.</summary>
public sealed class BoundInterpolatedStringExpression(ImmutableArray<BoundExpression> parts) : BoundExpression
{
    public ImmutableArray<BoundExpression> Parts { get; } = parts;

    public override TypeSymbol Type => TypeSymbol.String;
}

/// <summary>An implicit widening conversion (Int → Long, Int → Double, Long → Double).</summary>
public sealed class BoundConversionExpression(TypeSymbol type, BoundExpression expression) : BoundExpression
{
    public BoundExpression Expression { get; } = expression;

    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundIfExpression(BoundExpression condition, BoundBlockExpression then, BoundExpression? @else, TypeSymbol type)
    : BoundExpression
{
    public BoundExpression Condition { get; } = condition;

    public BoundBlockExpression Then { get; } = then;

    public BoundExpression? Else { get; } = @else;

    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundBlockExpression(ImmutableArray<BoundStatement> statements, BoundExpression? trailing, TypeSymbol type)
    : BoundExpression
{
    public ImmutableArray<BoundStatement> Statements { get; } = statements;

    public BoundExpression? Trailing { get; } = trailing;

    public override TypeSymbol Type { get; } = type;
}

/// <summary>Produced only by lowering: an if expression whose branches are plain expressions (C# <c>?:</c>).</summary>
public sealed class BoundConditionalExpression(BoundExpression condition, BoundExpression whenTrue, BoundExpression whenFalse, TypeSymbol type)
    : BoundExpression
{
    public BoundExpression Condition { get; } = condition;

    public BoundExpression WhenTrue { get; } = whenTrue;

    public BoundExpression WhenFalse { get; } = whenFalse;

    public override TypeSymbol Type { get; } = type;
}
