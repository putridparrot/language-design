using System.Collections.Immutable;

namespace Piper.Binding;

// The bound tree is the type-checked form of the program: names are resolved to symbols and
// every expression has a type. The binder produces it; the lowerer rewrites it into a subset the
// C# emitter can translate directly (no block or if expressions in value positions).

public abstract class BoundNode;

public sealed class BoundProgram(
    ImmutableArray<TypeSymbol> types,
    ImmutableArray<EnumSymbol> builtinTypes,
    ImmutableArray<BoundFunction> functions,
    ImmutableArray<BoundFunction> methods,
    FunctionSymbol? main)
{
    /// <summary>This compilation's built-in Option and Result (they may have gained methods from extend blocks).</summary>
    public ImmutableArray<EnumSymbol> BuiltinTypes { get; } = builtinTypes;

    /// <summary>User-declared records, enums and traits, in declaration order.</summary>
    public ImmutableArray<TypeSymbol> Types { get; } = types;

    /// <summary>Top-level functions.</summary>
    public ImmutableArray<BoundFunction> Functions { get; } = functions;

    /// <summary>Methods with bodies, from extend blocks and trait defaults; each symbol is a <see cref="MethodSymbol"/>.</summary>
    public ImmutableArray<BoundFunction> Methods { get; } = methods;

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

/// <summary>A call to a top-level function. <see cref="TypeArguments"/> are the inferred type arguments of a generic function.</summary>
public sealed class BoundCallExpression(
    FunctionSymbol function,
    ImmutableArray<TypeSymbol> typeArguments,
    ImmutableArray<BoundExpression> arguments,
    TypeSymbol type) : BoundExpression
{
    public FunctionSymbol Function { get; } = function;

    public ImmutableArray<TypeSymbol> TypeArguments { get; } = typeArguments;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type { get; } = type;
}

/// <summary>
/// <c>receiver.method(args)</c>, or <c>Type.method(args)</c> for a static method (<see cref="Receiver"/> is null).
/// <see cref="ReceiverType"/> is the receiver's (instantiated) type; <see cref="TypeArguments"/> are the method's own.
/// </summary>
public sealed class BoundMethodCallExpression(
    BoundExpression? receiver,
    TypeSymbol receiverType,
    MethodSymbol method,
    ImmutableArray<TypeSymbol> typeArguments,
    ImmutableArray<BoundExpression> arguments,
    TypeSymbol type) : BoundExpression
{
    public BoundExpression? Receiver { get; } = receiver;

    public TypeSymbol ReceiverType { get; } = receiverType;

    public MethodSymbol Method { get; } = method;

    public ImmutableArray<TypeSymbol> TypeArguments { get; } = typeArguments;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundRecordCreationExpression(RecordSymbol record, ImmutableArray<BoundExpression> arguments, TypeSymbol type)
    : BoundExpression
{
    public RecordSymbol Record { get; } = record;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    /// <summary>The record itself, or its instantiation for a generic record.</summary>
    public override TypeSymbol Type { get; } = type;
}

public sealed class BoundVariantCreationExpression(VariantSymbol variant, ImmutableArray<BoundExpression> arguments, TypeSymbol type)
    : BoundExpression
{
    public VariantSymbol Variant { get; } = variant;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    /// <summary>The enum itself, or its instantiation for a generic enum.</summary>
    public override TypeSymbol Type { get; } = type;
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

/// <summary><c>left ?? right</c>. <see cref="Right"/> has either the left's value type, or (chaining) the left's optional type.</summary>
public sealed class BoundNullCoalescingExpression(BoundExpression left, BoundExpression right, TypeSymbol type) : BoundExpression
{
    public BoundExpression Left { get; } = left;

    public BoundExpression Right { get; } = right;

    public override TypeSymbol Type { get; } = type;
}

/// <summary>
/// <c>target?.chain</c>: <see cref="WhenNotNull"/> refers to the unwrapped target through <see cref="Receiver"/>.
/// <see cref="Type"/> is Unit (used as a statement), the chain's own optional type (flattened), or <c>Option</c> of it.
/// </summary>
public sealed class BoundConditionalAccessExpression(
    BoundExpression target,
    VariableSymbol receiver,
    BoundExpression whenNotNull,
    TypeSymbol type) : BoundExpression
{
    public BoundExpression Target { get; } = target;

    public VariableSymbol Receiver { get; } = receiver;

    public BoundExpression WhenNotNull { get; } = whenNotNull;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>operand?</c>: the Option's or Result's value; otherwise returns None or the Err from <see cref="FunctionReturnType"/>.</summary>
public sealed class BoundTryExpression(BoundExpression operand, TypeSymbol type, TypeSymbol functionReturnType) : BoundExpression
{
    public BoundExpression Operand { get; } = operand;

    public TypeSymbol FunctionReturnType { get; } = functionReturnType;

    public override TypeSymbol Type { get; } = type;
}

/// <summary>Produced only by lowering: a member of the emitted Option/Result struct (IsSome, IsOk, Value or Error).</summary>
public sealed class BoundOptionalMemberExpression(BoundExpression operand, string member, TypeSymbol type) : BoundExpression
{
    public BoundExpression Operand { get; } = operand;

    public string Member { get; } = member;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>expression is pattern</c>, which also binds the pattern's variables. Also produced by lowering a match.</summary>
public sealed class BoundIsPatternExpression(BoundExpression expression, BoundPattern pattern) : BoundExpression
{
    public BoundExpression Expression { get; } = expression;

    public BoundPattern Pattern { get; } = pattern;

    public override TypeSymbol Type => TypeSymbol.Bool;
}

public sealed class BoundFieldAccessExpression(BoundExpression target, FieldSymbol field, TypeSymbol type) : BoundExpression
{
    public BoundExpression Target { get; } = target;

    public FieldSymbol Field { get; } = field;

    /// <summary>The field's type with the target's type arguments substituted.</summary>
    public override TypeSymbol Type { get; } = type;
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
