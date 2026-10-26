using System.Collections.Immutable;
using System.Reflection;
using Piper.Text;

namespace Piper.Binding;

// The bound tree is the type-checked form of the program: names are resolved to symbols and
// every expression has a type. The binder produces it; the lowerer rewrites it into a subset the
// C# emitter can translate directly (no block or if expressions in value positions).

public abstract class BoundNode
{
    /// <summary>
    /// Where in its Piper file the node came from, if known. Set by the binder on statements and expressions it binds
    /// from syntax, and copied by the lowerer onto what it produces, so the emitted C# can carry <c>#line</c> directives.
    /// </summary>
    public TextSpan? SourceSpan { get; internal set; }
}

public sealed class BoundProgram(
    ImmutableArray<SourceText> files,
    ImmutableArray<NamespaceSymbol> fileNamespaces,
    ImmutableArray<TypeSymbol> types,
    ImmutableArray<EnumSymbol> builtinTypes,
    ImmutableArray<BoundFunction> functions,
    ImmutableArray<BoundFunction> methods,
    FunctionSymbol? main)
{
    /// <summary>The program's source files; every declaration's <see cref="Symbol.DeclaringFile"/> is one of these.</summary>
    public ImmutableArray<SourceText> Files { get; } = files;

    /// <summary>The namespace each of <see cref="Files"/> declares (the global namespace if none).</summary>
    public ImmutableArray<NamespaceSymbol> FileNamespaces { get; } = fileNamespaces;

    /// <summary>This compilation's built-in Option and Result (they may have gained methods from extend blocks).</summary>
    public ImmutableArray<EnumSymbol> BuiltinTypes { get; } = builtinTypes;

    /// <summary>User-declared records, enums and traits, in declaration order.</summary>
    public ImmutableArray<TypeSymbol> Types { get; } = types;

    /// <summary>Top-level functions.</summary>
    public ImmutableArray<BoundFunction> Functions { get; } = functions;

    /// <summary>Methods with bodies, from extend blocks and trait defaults; each symbol is a <see cref="MethodSymbol"/>.</summary>
    public ImmutableArray<BoundFunction> Methods { get; } = methods;

    public FunctionSymbol? Main { get; } = main;

    /// <summary>Traits implemented by builtin, .NET and runtime types, which are boxed to be used as the trait.</summary>
    public ImmutableArray<ExternalConformance> ExternalConformances { get; init; } = [];

    /// <summary>`test` blocks, which are only compiled in for `piper test` (see <see cref="WithTestsAsFunctions"/>).</summary>
    public ImmutableArray<BoundTest> Tests { get; init; } = [];

    /// <summary>The program with its tests added to its functions, so they're emitted (each marked with PiperTestAttribute).</summary>
    public BoundProgram WithTestsAsFunctions() =>
        new(Files, FileNamespaces, Types, BuiltinTypes, [.. Functions, .. Tests.Select(t => t.Function)], Methods, Main)
        {
            ExternalConformances = ExternalConformances,
            Tests = Tests,
        };
}

/// <summary>A `test "name" { … }` block, as a parameterless function.</summary>
public sealed record BoundTest(string Name, BoundFunction Function);

/// <summary>
/// `extend int: Shape`, in <paramref name="File"/>. For Option or Result, <paramref name="Target"/> is the definition and
/// <paramref name="Trait"/> is written in terms of its type parameters.
/// </summary>
public sealed record ExternalConformance(TypeSymbol Target, TypeSymbol Trait, SourceText File);

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

/// <summary>
/// <see cref="Initializer"/> is null only for declarations introduced by lowering (assigned on every path afterwards).
/// <see cref="IsUsing"/>: <c>using let r = …;</c>, disposed at the end of the enclosing block (C#'s using declaration).
/// </summary>
public sealed class BoundVariableDeclaration(VariableSymbol variable, BoundExpression? initializer, bool isUsing = false) : BoundStatement
{
    public VariableSymbol Variable { get; } = variable;

    public BoundExpression? Initializer { get; } = initializer;

    public bool IsUsing { get; } = isUsing;

    /// <summary>`await using`: disposed with DisposeAsync.</summary>
    public bool IsAwait { get; init; }
}

/// <summary><c>using (let r = …) { … }</c> or <c>using (expression) { … }</c>: disposes the resource after the body.</summary>
public sealed class BoundUsingStatement(VariableSymbol? variable, BoundExpression resource, BoundStatement body) : BoundStatement
{
    public VariableSymbol? Variable { get; } = variable;

    public BoundExpression Resource { get; } = resource;

    public BoundStatement Body { get; } = body;

    /// <summary>`await using`: disposed with DisposeAsync.</summary>
    public bool IsAwait { get; init; }
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

    /// <summary>`while true` with no `break` out of it: control never continues past the loop.</summary>
    public bool IsInfinite { get; init; }
}

/// <summary>A counted loop over <c>Lower..Upper</c>, including <see cref="Upper"/> when <see cref="IsInclusive"/> (<c>Lower..&lt;Upper</c> excludes it).</summary>
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

/// <summary>A local function's declaration, where it stands in its block.</summary>
public sealed class BoundLocalFunctionStatement(BoundFunction function) : BoundStatement
{
    public BoundFunction Function { get; } = function;
}

/// <summary>
/// Stands for a value that's never produced, made by lowering: in <c>F(a, { throw E() })</c> the throw becomes a
/// statement before the call, which is then never reached, and the argument becomes this (C#'s <c>default!</c>).
/// </summary>
public sealed class BoundUnreachableExpression : BoundExpression
{
    public override TypeSymbol Type => TypeSymbol.Never;
}

/// <summary>Produced only by lowering: a place <see cref="BoundGotoStatement"/>s jump forward to, past code they skip.</summary>
public sealed class BoundLabel(string name = "end")
{
    /// <summary>What the label marks; the emitter adds a number to make it unique.</summary>
    public string Name { get; } = name;
}

/// <summary><c>label: ;</c>, produced only by lowering.</summary>
public sealed class BoundLabelStatement(BoundLabel label) : BoundStatement
{
    public BoundLabel Label { get; } = label;
}

/// <summary><c>goto label;</c>, produced only by lowering: always forward, out of the blocks it is in.</summary>
public sealed class BoundGotoStatement(BoundLabel label) : BoundStatement
{
    public BoundLabel Label { get; } = label;
}

/// <summary>
/// <c>for x in collection</c> over anything enumerable (C# <c>foreach</c>); with <see cref="Deconstruction"/>,
/// <c>for (k, v) in dictionary</c>, and then <see cref="Variable"/> is unused.
/// </summary>
public sealed class BoundForEachStatement(
    VariableSymbol variable,
    BoundExpression collection,
    BoundStatement body,
    BoundParenthesizedDesignation? deconstruction = null) : BoundStatement
{
    public VariableSymbol Variable { get; } = variable;

    public BoundParenthesizedDesignation? Deconstruction { get; } = deconstruction;

    public BoundExpression Collection { get; } = collection;

    public BoundStatement Body { get; } = body;

    /// <summary>`await for`: over an IAsyncEnumerable (C#'s `await foreach`).</summary>
    public bool IsAwait { get; init; }
}

public sealed class BoundTryCatchStatement(BoundStatement body, ImmutableArray<BoundCatchClause> catches, BoundStatement? @finally)
    : BoundStatement
{
    public BoundStatement Body { get; } = body;

    public ImmutableArray<BoundCatchClause> Catches { get; } = catches;

    public BoundStatement? Finally { get; } = @finally;
}

/// <summary>
/// <c>defer body;</c>. Only seen while binding its block, which turns the statements after it into a try whose finally is
/// <see cref="Body"/>.
/// </summary>
public sealed class BoundDeferStatement(BoundStatement body) : BoundStatement
{
    public BoundStatement Body { get; } = body;
}

/// <summary><see cref="ExceptionType"/> is null for a bare <c>catch</c>; <see cref="Variable"/> is null when nothing is bound.</summary>
public sealed class BoundCatchClause(TypeSymbol? exceptionType, VariableSymbol? variable, BoundStatement body)
{
    public TypeSymbol? ExceptionType { get; } = exceptionType;

    public VariableSymbol? Variable { get; } = variable;

    public BoundStatement Body { get; } = body;
}

/// <summary>Produced only by lowering: <c>throw e;</c>.</summary>
public sealed class BoundThrowStatement(BoundExpression exception) : BoundStatement
{
    public BoundExpression Exception { get; } = exception;
}

public sealed class BoundBreakStatement : BoundStatement;

/// <summary>`yield return value;`, or `yield break;` when <see cref="Value"/> is null.</summary>
public sealed class BoundYieldStatement(BoundExpression? value) : BoundStatement
{
    public BoundExpression? Value { get; } = value;
}

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
    /// <summary>The names of named arguments (`retries: 5`), by position; empty when all are positional.</summary>
    public ImmutableArray<string?> ArgumentNames { get; init; } = [];

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
    /// <summary>The names of named arguments (`retries: 5`), by position; empty when all are positional.</summary>
    public ImmutableArray<string?> ArgumentNames { get; init; } = [];

    public BoundExpression? Receiver { get; } = receiver;

    public TypeSymbol ReceiverType { get; } = receiverType;

    public MethodSymbol Method { get; } = method;

    public ImmutableArray<TypeSymbol> TypeArguments { get; } = typeArguments;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type { get; } = type;

    /// <summary>For a trait method, the trait it is called through as instantiated for the receiver: <c>Convert&lt;int&gt;</c>.</summary>
    public TypeSymbol? TraitType { get; init; }
}

/// <summary><c>Account("Ada", 100m)</c>: a new instance of a class, through its primary constructor.</summary>
public sealed class BoundObjectCreationExpression(ClassSymbol @class, ImmutableArray<BoundExpression> arguments, TypeSymbol type) : BoundExpression
{
    /// <summary>The names of named arguments, by position; empty when all are positional.</summary>
    public ImmutableArray<string?> ArgumentNames { get; init; } = [];

    public ClassSymbol Class { get; } = @class;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    /// <summary>The class itself, or its instantiation for a generic class.</summary>
    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>other.balance = 5</c>, <c>this.count += 1</c>: setting a class's mutable field through an instance.</summary>
public sealed class BoundFieldAssignmentExpression(BoundExpression target, FieldSymbol field, BoundBinaryOperatorKind? compoundOperator, BoundExpression value)
    : BoundExpression
{
    public BoundExpression Target { get; } = target;

    public FieldSymbol Field { get; } = field;

    public BoundBinaryOperatorKind? CompoundOperator { get; } = compoundOperator;

    public BoundExpression Value { get; } = value;

    public override TypeSymbol Type => Value.Type;
}

public sealed class BoundRecordCreationExpression(RecordSymbol record, ImmutableArray<BoundExpression> arguments, TypeSymbol type)
    : BoundExpression
{
    /// <summary>The names of named arguments (`retries: 5`), by position; empty when all are positional.</summary>
    public ImmutableArray<string?> ArgumentNames { get; init; } = [];

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

// Lambdas and function values

/// <summary>
/// A lambda; <see cref="Type"/> is its delegate type. Before lowering <see cref="Body"/> is a
/// <see cref="BoundBlockExpression"/>, after it a <see cref="BoundBlockStatement"/>. <see cref="BodyType"/> is the
/// type of the body's own value (before conversion to <see cref="ReturnType"/>), used to rank overloads.
/// </summary>
public sealed class BoundLambdaExpression(
    ImmutableArray<VariableSymbol> parameters,
    BoundNode body,
    TypeSymbol returnType,
    TypeSymbol bodyType,
    bool isExpressionBodied,
    TypeSymbol type) : BoundExpression
{
    public ImmutableArray<VariableSymbol> Parameters { get; } = parameters;

    public BoundNode Body { get; } = body;

    public TypeSymbol ReturnType { get; } = returnType;

    public TypeSymbol BodyType { get; } = bodyType;

    public bool IsExpressionBodied { get; } = isExpressionBodied;

    /// <summary>`async x => …`: <see cref="Type"/> returns a Task, and <see cref="ReturnType"/> is the Task's result.</summary>
    public bool IsAsync { get; init; }

    public override TypeSymbol Type { get; } = type;
}

/// <summary>A top-level function used as a value (a C# method group converted to <see cref="Type"/>).</summary>
public sealed class BoundFunctionReferenceExpression(FunctionSymbol function, ImmutableArray<TypeSymbol> typeArguments, TypeSymbol type)
    : BoundExpression
{
    public FunctionSymbol Function { get; } = function;

    /// <summary>A generic function's type arguments: <c>identity&lt;int&gt;</c>.</summary>
    public ImmutableArray<TypeSymbol> TypeArguments { get; } = typeArguments;

    public override TypeSymbol Type { get; } = type;
}

/// <summary>Calling a function value: <c>f(1)</c> where f has a delegate type.</summary>
public sealed class BoundDelegateInvocationExpression(BoundExpression target, ImmutableArray<BoundExpression> arguments, TypeSymbol type)
    : BoundExpression
{
    public BoundExpression Target { get; } = target;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>await task</c>: <see cref="Type"/> is the awaited result (unit for a plain Task).</summary>
public sealed class BoundAwaitExpression(BoundExpression operand, TypeSymbol type) : BoundExpression
{
    public BoundExpression Operand { get; } = operand;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>throw e</c>: never completes, so its type is Never.</summary>
public sealed class BoundThrowExpression(BoundExpression exception) : BoundExpression
{
    public BoundExpression Exception { get; } = exception;

    public override TypeSymbol Type => TypeSymbol.Never;
}

/// <summary>
/// <c>(Int)x</c>: an explicit conversion, written as a C# cast. (Implicit conversions are
/// <see cref="BoundConversionExpression"/>s, which C# performs without a cast.)
/// </summary>
public sealed class BoundCastExpression(TypeSymbol type, BoundExpression operand) : BoundExpression
{
    public BoundExpression Operand { get; } = operand;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>(1, "a")</c>: a tuple literal.</summary>
public sealed class BoundTupleExpression(ImmutableArray<BoundExpression> elements, TupleTypeSymbol type) : BoundExpression
{
    public ImmutableArray<BoundExpression> Elements { get; } = elements;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>t.Item1</c> or <c>t.x</c>: element <see cref="Index"/> of a tuple (always emitted as <c>ItemN</c>).</summary>
public sealed class BoundTupleElementExpression(BoundExpression tuple, int index, TypeSymbol type) : BoundExpression
{
    public BoundExpression Tuple { get; } = tuple;

    public int Index { get; } = index;

    public override TypeSymbol Type { get; } = type;
}

/// <summary>
/// <c>let (a, b) = value</c> (<see cref="Declares"/>) or <c>(a, b) = value</c>: C#'s deconstruction, over a tuple, a
/// record or a .NET type with a <c>Deconstruct</c> method. Has type Unit.
/// </summary>
public sealed class BoundDeconstructionExpression(BoundParenthesizedDesignation designation, BoundExpression value, bool declares)
    : BoundExpression
{
    public BoundParenthesizedDesignation Designation { get; } = designation;

    public BoundExpression Value { get; } = value;

    public bool Declares { get; } = declares;

    public override TypeSymbol Type => TypeSymbol.Unit;
}

/// <summary>Where one part of a deconstructed value goes. <see cref="Type"/> is that part's type.</summary>
public abstract class BoundDesignation(TypeSymbol type)
{
    public TypeSymbol Type { get; } = type;
}

/// <summary>A variable: declared by the deconstruction, or an existing (mutable) one assigned by it.</summary>
public sealed class BoundVariableDesignation(VariableSymbol variable, TypeSymbol type) : BoundDesignation(type)
{
    public VariableSymbol Variable { get; } = variable;
}

/// <summary><c>_</c>: the part is not stored.</summary>
public sealed class BoundDiscardDesignation(TypeSymbol type) : BoundDesignation(type);

/// <summary><c>(a, (b, c))</c>: the part is deconstructed further.</summary>
public sealed class BoundParenthesizedDesignation(ImmutableArray<BoundDesignation> elements, TypeSymbol type) : BoundDesignation(type)
{
    public ImmutableArray<BoundDesignation> Elements { get; } = elements;

    public IEnumerable<VariableSymbol> Variables => Elements.SelectMany(e => e switch
    {
        BoundVariableDesignation variable => [variable.Variable],
        BoundParenthesizedDesignation nested => nested.Variables,
        _ => [],
    });
}

/// <summary>`{ ...source, y: 5 }`: a copy of a record with some fields changed (emitted as C#'s `source with { y = 5 }`).</summary>
public sealed class BoundRecordSpreadExpression(BoundExpression source, ImmutableArray<(FieldSymbol Field, BoundExpression Value)> updates)
    : BoundExpression
{
    public BoundExpression Source { get; } = source;

    /// <summary>In the order written, which is the order the values are evaluated in.</summary>
    public ImmutableArray<(FieldSymbol Field, BoundExpression Value)> Updates { get; } = updates;

    public override TypeSymbol Type => Source.Type;
}

/// <summary>
/// <c>Window() { Title: "x" }</c>: a new object with some of its properties and fields set (emitted as C#'s object
/// initializer). <see cref="Creation"/> is a <see cref="BoundObjectCreationExpression"/> or a .NET constructor's
/// <see cref="BoundClrInvocationExpression"/>.
/// </summary>
public sealed class BoundObjectInitializerExpression(BoundExpression creation, ImmutableArray<(string Member, BoundExpression Value)> members)
    : BoundExpression
{
    public BoundExpression Creation { get; } = creation;

    /// <summary>In the order written, which is the order the values are evaluated and set in.</summary>
    public ImmutableArray<(string Member, BoundExpression Value)> Members { get; } = members;

    public override TypeSymbol Type => Creation.Type;
}

/// <summary><c>Int[5]</c> or <c>Double[2, 3]</c>: a new array of default values (C#'s <c>new int[5]</c>).</summary>
public sealed class BoundArrayCreationExpression(ImmutableArray<BoundExpression> sizes, TypeSymbol type) : BoundExpression
{
    public ImmutableArray<BoundExpression> Sizes { get; } = sizes;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>[a, b, c]</c> as a <c>List&lt;T&gt;</c> or a .NET array (<see cref="Type"/> says which).</summary>
public sealed class BoundCollectionExpression(ImmutableArray<BoundExpression> elements, TypeSymbol type) : BoundExpression
{
    public ImmutableArray<BoundExpression> Elements { get; } = elements;

    public override TypeSymbol Type { get; } = type;

    public bool HasSpread => Elements.Any(e => e is BoundSpreadElement);
}

/// <summary><c>...items</c> in a collection literal (C#'s <c>..items</c>); <see cref="Type"/> is the sequence's.</summary>
public sealed class BoundSpreadElement(BoundExpression source) : BoundExpression
{
    public BoundExpression Source { get; } = source;

    public override TypeSymbol Type => Source.Type;
}

// .NET interop

/// <summary>
/// A call to a .NET method or constructor (<see cref="Member"/> is a MethodInfo or ConstructorInfo).
/// <see cref="Receiver"/> is null for static methods (including extension methods, whose receiver is the
/// first argument) and constructors; <see cref="ContainerType"/> is the type they belong to.
/// <see cref="TypeArguments"/> are a generic method's inferred type arguments.
/// </summary>
public sealed class BoundClrInvocationExpression(
    BoundExpression? receiver,
    TypeSymbol containerType,
    MethodBase member,
    ImmutableArray<BoundExpression> arguments,
    TypeSymbol type,
    ImmutableArray<TypeSymbol> typeArguments = default) : BoundExpression
{
    /// <summary>The names of named arguments (`count: 2`), by position; empty when all are positional.</summary>
    public ImmutableArray<string?> ArgumentNames { get; init; } = [];

    public BoundExpression? Receiver { get; } = receiver;

    public TypeSymbol ContainerType { get; } = containerType;

    public MethodBase Member { get; } = member;

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public ImmutableArray<TypeSymbol> TypeArguments { get; } = typeArguments.IsDefault ? [] : typeArguments;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>ref x</c> or <c>ref items[i]</c>, for a .NET <c>ref</c> parameter: <see cref="Target"/> is passed by reference.</summary>
public sealed class BoundRefArgument(BoundExpression target) : BoundExpression
{
    /// <summary>A mutable variable, or an element of an array.</summary>
    public BoundExpression Target { get; } = target;

    public override TypeSymbol Type => Target.Type;
}

/// <summary>
/// An argument for a .NET <c>out</c> parameter: <see cref="Variable"/> receives the value (null for <c>out _</c>).
/// When <see cref="Declares"/>, the argument also declares it (lowering hoists the declaration before the call).
/// A <see cref="Boundary"/> other than None means .NET may produce null, so the variable is an Option and the
/// emitted code converts after the call.
/// </summary>
public sealed class BoundOutArgument(VariableSymbol? variable, bool declares, TypeSymbol clrType, ClrBoundary boundary, TypeSymbol type)
    : BoundExpression
{
    public VariableSymbol? Variable { get; } = variable;

    public bool Declares { get; } = declares;

    /// <summary>The type .NET writes (the Option's value type when there is a boundary).</summary>
    public TypeSymbol ClrType { get; } = clrType;

    public ClrBoundary Boundary { get; } = boundary;

    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>target[arguments]</c>: an array element or a .NET indexer (<see cref="Indexer"/> is null for arrays).</summary>
public sealed class BoundIndexExpression(BoundExpression target, PropertyInfo? indexer, ImmutableArray<BoundExpression> arguments, TypeSymbol type)
    : BoundExpression
{
    public BoundExpression Target { get; } = target;

    public PropertyInfo? Indexer { get; } = indexer;

    /// <summary>A Piper class's indexer, when that's what it uses (<see cref="Indexer"/> is then null).</summary>
    public IndexerSymbol? PiperIndexer { get; init; }

    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override TypeSymbol Type { get; } = type;
}

/// <summary>Assigning a .NET property or indexer: <c>sb.Length = 0</c>, <c>ages["Ada"] += 1</c>.</summary>
public sealed class BoundClrAssignmentExpression(BoundExpression target, BoundBinaryOperatorKind? compoundOperator, BoundExpression value)
    : BoundExpression
{
    /// <summary>A <see cref="BoundIndexExpression"/> or <see cref="BoundClrMemberExpression"/>.</summary>
    public BoundExpression Target { get; } = target;

    public BoundBinaryOperatorKind? CompoundOperator { get; } = compoundOperator;

    public BoundExpression Value { get; } = value;

    public override TypeSymbol Type => TypeSymbol.Unit;
}

/// <summary>Reading a .NET property or field (<see cref="Receiver"/> is null for static ones).</summary>
public sealed class BoundClrMemberExpression(BoundExpression? receiver, TypeSymbol containerType, MemberInfo member, TypeSymbol type)
    : BoundExpression
{
    public BoundExpression? Receiver { get; } = receiver;

    public TypeSymbol ContainerType { get; } = containerType;

    public MemberInfo Member { get; } = member;

    public override TypeSymbol Type { get; } = type;
}

public enum NullableBoundaryKind
{
    /// <summary>A possibly-null .NET reference → Option.</summary>
    FromNullableReference,

    /// <summary>A .NET <c>Nullable&lt;T&gt;</c> → Option.</summary>
    FromNullableValue,

    /// <summary>An Option passed where .NET accepts a null reference.</summary>
    ToNullableReference,

    /// <summary>An Option passed where .NET expects a <c>Nullable&lt;T&gt;</c>.</summary>
    ToNullableValue,
}

/// <summary>Where null meets Option: .NET's null becomes None on the way in, and None becomes null on the way out.</summary>
public sealed class BoundNullableBoundaryExpression(BoundExpression operand, NullableBoundaryKind kind, TypeSymbol type) : BoundExpression
{
    public BoundExpression Operand { get; } = operand;

    public NullableBoundaryKind Kind { get; } = kind;

    public override TypeSymbol Type { get; } = type;
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

/// <summary>
/// <c>[Obsolete("…")]</c>: an attribute of .NET type <see cref="Type"/>, with constant constructor <see cref="Arguments"/>
/// and <see cref="NamedArguments"/> for its properties and fields. <see cref="Target"/> is C#'s target specifier
/// (<c>property</c> for a record field's attribute that applies to properties), or null.
/// </summary>
public sealed record BoundAttribute(
    TypeSymbol Type,
    ImmutableArray<BoundExpression> Arguments,
    ImmutableArray<(string Name, BoundExpression Value)> NamedArguments,
    string? Target = null);

/// <summary>
/// A `requires` condition: <see cref="Condition"/> bound in the function's (or record's) scope, its source
/// <see cref="Text"/>, and the parameters (or fields) it mentions, whose values a failure reports.
/// </summary>
public sealed record BoundContract(BoundExpression Condition, string Text, ImmutableArray<VariableSymbol> Mentioned, TextSpan Span);

/// <summary>A hole of an interpolated string with C#'s alignment and format: <c>{price,10:C}</c>.</summary>
public sealed class BoundFormattedExpression(BoundExpression value, int? alignment, string? format) : BoundExpression
{
    public BoundExpression Value { get; } = value;

    public int? Alignment { get; } = alignment;

    public string? Format { get; } = format;

    public override TypeSymbol Type => TypeSymbol.String;
}

/// <summary>
/// `let Pattern = Initializer else { … };`: binds the pattern's variables, or runs <see cref="Else"/> (which leaves).
/// Without an else, the pattern matches every value.
/// </summary>
public sealed class BoundLetElseStatement(BoundPattern pattern, BoundExpression initializer, BoundBlockExpression? @else) : BoundStatement
{
    public BoundPattern Pattern { get; } = pattern;

    public BoundExpression Initializer { get; } = initializer;

    public BoundBlockExpression? Else { get; } = @else;
}
