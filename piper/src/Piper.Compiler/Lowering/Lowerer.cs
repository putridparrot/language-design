using System.Collections.Immutable;
using Piper.Binding;

namespace Piper.Lowering;

/// <summary>
/// Rewrites the bound tree into a form that maps directly onto C#. Piper's blocks and ifs are
/// expressions, C#'s are not, so a value-producing <c>if</c> or block becomes either a ternary
/// (when its branches are plain expressions) or statements that store into a temporary / return.
///
/// Statements hoisted out of an expression run before it, so to preserve left-to-right evaluation
/// any operand evaluated earlier is spilled into a temporary first, and the right side of
/// <c>&amp;&amp;</c>/<c>||</c> stays conditional.
/// </summary>
public sealed class Lowerer
{
    /// <summary>While lowering a <c>?.</c> chain, its receiver variable stands for <c>target.Value</c>.</summary>
    private readonly Dictionary<VariableSymbol, BoundExpression> _substitutions = [];

    private Lowerer()
    {
    }

    public static BoundProgram Lower(BoundProgram program)
    {
        var lowerer = new Lowerer();
        ImmutableArray<BoundFunction> LowerAll(ImmutableArray<BoundFunction> functions) =>
            [.. functions.Select(f => new BoundFunction(f.Symbol, lowerer.LowerFunction(f), f.IsExpressionBodied))];
        return new BoundProgram(program.Types, program.BuiltinTypes, LowerAll(program.Functions), LowerAll(program.Methods), program.Main);
    }

    private BoundBlockStatement LowerFunction(BoundFunction function)
    {
        var body = (BoundBlockExpression)function.Body;
        var output = new List<BoundStatement>();
        if (function.Symbol.ReturnType == TypeSymbol.Unit)
        {
            foreach (var statement in body.Statements)
                LowerStatement(statement, output);
            if (body.Trailing is not null)
                LowerForEffect(body.Trailing, output);
        }
        else
        {
            LowerInto(body, value => new BoundReturnStatement(value), output);
        }
        return new BoundBlockStatement([.. output]);
    }

    // Statements

    private void LowerStatement(BoundStatement statement, List<BoundStatement> output)
    {
        switch (statement)
        {
            case BoundVariableDeclaration { Initializer: { } initializer } declaration:
                if (initializer is BoundIfExpression or BoundBlockExpression or BoundMatchExpression && !TryLowerPure(initializer, out _))
                {
                    // `let x = if c { ...; a } else { b };` → `T x; if (c) { ...; x = a; } else { x = b; }`
                    output.Add(new BoundVariableDeclaration(declaration.Variable, null));
                    LowerInto(initializer, value => Assign(declaration.Variable, value), output);
                }
                else
                {
                    output.Add(new BoundVariableDeclaration(declaration.Variable, LowerForValue(initializer, output)));
                }
                break;

            case BoundExpressionStatement expressionStatement:
                LowerForEffect(expressionStatement.Expression, output);
                break;

            case BoundReturnStatement { Value: { } value }:
                LowerInto(value, v => new BoundReturnStatement(v), output);
                break;

            case BoundWhileStatement loop:
                LowerWhile(loop, output);
                break;

            case BoundForStatement loop:
                LowerFor(loop, output);
                break;

            case BoundBlockStatement block:
                output.Add(LowerBlockStatement(block.Statements));
                break;

            default:
                output.Add(statement);
                break;
        }
    }

    private void LowerWhile(BoundWhileStatement loop, List<BoundStatement> output)
    {
        var conditionPrelude = new List<BoundStatement>();
        var condition = LowerForValue(loop.Condition, conditionPrelude);
        var body = LowerToStatement(loop.Body);

        if (conditionPrelude.Count == 0)
        {
            output.Add(new BoundWhileStatement(condition, body));
            return;
        }

        // The condition needs statements: `while (true) { prelude; if (!cond) break; body }`.
        var inner = new List<BoundStatement>(conditionPrelude)
        {
            new BoundIfStatement(Not(condition), new BoundBreakStatement(), null),
        };
        inner.AddRange(Flatten(body));
        output.Add(new BoundWhileStatement(new BoundLiteralExpression(true, TypeSymbol.Bool), new BoundBlockStatement([.. inner])));
    }

    /// <summary>The upper bound is evaluated once, before the loop (C# would re-evaluate it every iteration).</summary>
    private void LowerFor(BoundForStatement loop, List<BoundStatement> output)
    {
        var bounds = LowerOperands([loop.Lower, loop.Upper], output);
        var lower = bounds[0];
        var upper = bounds[1];

        if (!IsInvariant(upper))
        {
            if (!IsConstant(lower))
                lower = Spill(lower, output);
            upper = Spill(upper, output);
        }

        output.Add(new BoundForStatement(loop.Variable, lower, upper, loop.IsInclusive, LowerToStatement(loop.Body)));
    }

    private BoundStatement LowerToStatement(BoundStatement statement)
    {
        var output = new List<BoundStatement>();
        LowerStatement(statement, output);
        return output is [BoundBlockStatement block] ? block : new BoundBlockStatement([.. output]);
    }

    private BoundBlockStatement LowerBlockStatement(IEnumerable<BoundStatement> statements)
    {
        var output = new List<BoundStatement>();
        foreach (var statement in statements)
            LowerStatement(statement, output);
        return new BoundBlockStatement([.. output]);
    }

    // Expressions evaluated only for their side effects

    private void LowerForEffect(BoundExpression expression, List<BoundStatement> output)
    {
        switch (expression)
        {
            case BoundIfExpression @if:
            {
                var condition = LowerForValue(@if.Condition, output);
                var then = BlockForEffect(@if.Then);
                BoundStatement? @else = null;
                if (@if.Else is not null)
                {
                    var elseOutput = new List<BoundStatement>();
                    LowerForEffect(@if.Else, elseOutput);
                    @else = elseOutput is [BoundStatement single and (BoundIfStatement or BoundBlockStatement)]
                        ? single
                        : new BoundBlockStatement([.. elseOutput]);
                }
                output.Add(new BoundIfStatement(condition, then, @else));
                break;
            }

            case BoundBlockExpression block:
                output.Add(BlockForEffect(block));
                break;

            case BoundMatchExpression match:
                LowerMatch(match, sink: null, output);
                break;

            case BoundConditionalAccessExpression { Type: var type } access when type == TypeSymbol.Unit:
            {
                // `user?.greet();` → `if (user.IsSome) { user.Value.greet(); }`
                var target = LowerOptionalOperand(access.Target, output);
                var body = new List<BoundStatement>();
                WithReceiver(access, target, () => LowerForEffect(access.WhenNotNull, body));
                output.Add(new BoundIfStatement(HasValue(target), new BoundBlockStatement([.. body]), null));
                break;
            }

            case BoundConversionExpression conversion:
                LowerForEffect(conversion.Expression, output);
                break;

            case BoundLiteralExpression or BoundVariableExpression:
                break;

            default:
                output.Add(new BoundExpressionStatement(LowerForValue(expression, output)));
                break;
        }
    }

    private BoundBlockStatement BlockForEffect(BoundBlockExpression block)
    {
        var output = new List<BoundStatement>();
        foreach (var statement in block.Statements)
            LowerStatement(statement, output);
        if (block.Trailing is not null)
            LowerForEffect(block.Trailing, output);
        return new BoundBlockStatement([.. output]);
    }

    // Expressions whose value is consumed by a statement (assignment to a variable, or return)

    private void LowerInto(BoundExpression expression, Func<BoundExpression, BoundStatement> sink, List<BoundStatement> output)
    {
        switch (expression)
        {
            case BoundIfExpression { Else: not null } @if when !TryLowerPure(@if, out _):
                output.Add(IfInto(LowerForValue(@if.Condition, output), @if, sink));
                break;

            case BoundBlockExpression block:
                foreach (var statement in block.Statements)
                    LowerStatement(statement, output);
                // A block without a trailing value here has type Never: every path already returned or jumped.
                if (block.Trailing is not null)
                    LowerInto(block.Trailing, sink, output);
                break;

            case BoundMatchExpression match:
                LowerMatch(match, sink, output);
                break;

            case BoundConversionExpression { Expression: BoundIfExpression or BoundBlockExpression or BoundMatchExpression } conversion:
                LowerInto(conversion.Expression, value => sink(Widen(value, conversion.Type)), output);
                break;

            default:
                output.Add(sink(LowerForValue(expression, output)));
                break;
        }
    }

    // Expressions whose value is used inside another expression

    /// <summary>
    /// Returns an expression with no blocks or ifs in it; statements that must run first are appended to <paramref name="prelude"/>.
    /// </summary>
    private BoundExpression LowerForValue(BoundExpression expression, List<BoundStatement> prelude)
    {
        switch (expression)
        {
            case BoundVariableExpression variable when _substitutions.TryGetValue(variable.Variable, out var substitute):
                return substitute;

            case BoundLiteralExpression or BoundVariableExpression or BoundErrorExpression or BoundOptionalMemberExpression:
                return expression;

            case BoundConversionExpression conversion:
                return Widen(LowerForValue(conversion.Expression, prelude), conversion.Type);

            case BoundIsPatternExpression test:
                return new BoundIsPatternExpression(LowerForValue(test.Expression, prelude), test.Pattern);

            case BoundNullCoalescingExpression coalescing:
                return LowerNullCoalescing(coalescing, prelude);

            case BoundConditionalAccessExpression access:
                return LowerConditionalAccess(access, prelude);

            case BoundTryExpression attempt:
                return LowerTry(attempt, prelude);

            case BoundUnaryExpression unary:
                return new BoundUnaryExpression(unary.Operator, LowerForValue(unary.Operand, prelude), unary.Type);

            case BoundBinaryExpression { Operator: BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr } logical:
                return LowerShortCircuit(logical, prelude);

            case BoundBinaryExpression binary:
            {
                var operands = LowerOperands([binary.Left, binary.Right], prelude);
                return new BoundBinaryExpression(operands[0], binary.Operator, operands[1], binary.Type);
            }

            case BoundCallExpression call:
                return new BoundCallExpression(call.Function, call.TypeArguments, LowerOperands(call.Arguments, prelude), call.Type);

            case BoundMethodCallExpression { Receiver: { } receiver } call:
            {
                // The receiver is evaluated before the arguments.
                var operands = LowerOperands([receiver, .. call.Arguments], prelude);
                return new BoundMethodCallExpression(
                    operands[0], call.ReceiverType, call.Method, call.TypeArguments, operands[1..], call.Type);
            }

            case BoundMethodCallExpression call:
                return new BoundMethodCallExpression(
                    null, call.ReceiverType, call.Method, call.TypeArguments, LowerOperands(call.Arguments, prelude), call.Type);

            case BoundRecordCreationExpression creation:
                return new BoundRecordCreationExpression(creation.Record, LowerOperands(creation.Arguments, prelude), creation.Type);

            case BoundVariantCreationExpression creation:
                return new BoundVariantCreationExpression(creation.Variant, LowerOperands(creation.Arguments, prelude), creation.Type);

            case BoundMatchExpression match:
            {
                var temp = NewTemp(match.Type);
                prelude.Add(new BoundVariableDeclaration(temp, null));
                LowerMatch(match, value => Assign(temp, value), prelude);
                return new BoundVariableExpression(temp);
            }

            case BoundFieldAccessExpression access:
                return new BoundFieldAccessExpression(LowerForValue(access.Target, prelude), access.Field, access.Type);

            case BoundInterpolatedStringExpression interpolated:
                return new BoundInterpolatedStringExpression(LowerOperands(interpolated.Parts, prelude));

            case BoundAssignmentExpression assignment:
                return LowerAssignment(assignment, prelude);

            case BoundIfExpression @if:
                return LowerIfForValue(@if, prelude);

            case BoundBlockExpression block:
                foreach (var statement in block.Statements)
                    LowerStatement(statement, prelude);
                // Only reachable for blocks of type Never, whose value is never observed.
                return block.Trailing is null ? new BoundErrorExpression() : LowerForValue(block.Trailing, prelude);

            default:
                throw new InvalidOperationException($"Unexpected expression {expression.GetType().Name}");
        }
    }

    private BoundExpression LowerIfForValue(BoundIfExpression @if, List<BoundStatement> prelude)
    {
        var condition = LowerForValue(@if.Condition, prelude);
        if (@if.Else is not null &&
            TryLowerPure(@if.Then, out var whenTrue) &&
            TryLowerPure(@if.Else, out var whenFalse))
        {
            return new BoundConditionalExpression(condition, whenTrue, whenFalse, @if.Type);
        }

        var temp = NewTemp(@if.Type);
        prelude.Add(new BoundVariableDeclaration(temp, null));
        prelude.Add(IfInto(condition, @if, value => Assign(temp, value)));
        return new BoundVariableExpression(temp);
    }

    /// <summary>Builds <c>if (condition) { ...; sink(a) } else { ...; sink(b) }</c> from an already-lowered condition.</summary>
    private BoundIfStatement IfInto(BoundExpression condition, BoundIfExpression @if, Func<BoundExpression, BoundStatement> sink)
    {
        var then = new List<BoundStatement>();
        LowerInto(@if.Then, sink, then);
        var @else = new List<BoundStatement>();
        LowerInto(@if.Else!, sink, @else);
        return new BoundIfStatement(
            condition,
            new BoundBlockStatement([.. then]),
            @else is [BoundIfStatement elseIf] ? elseIf : new BoundBlockStatement([.. @else]));
    }

    /// <summary>
    /// <c>match s { P1 if g => a, P2 => b, P3 => c }</c> becomes
    /// <c>if (s is P1 &amp;&amp; g) { a } else if (s is P2) { b } else { c }</c>, with each arm's value passed to
    /// <paramref name="sink"/> (or evaluated for effect when it is null). The binder has proved the match
    /// exhaustive, so a final arm without guard or bindings needs no test; otherwise the chain ends in
    /// an unreachable throw, which also satisfies C#'s definite-assignment rules.
    /// </summary>
    private void LowerMatch(BoundMatchExpression match, Func<BoundExpression, BoundStatement>? sink, List<BoundStatement> output)
    {
        // The scrutinee is tested once per arm, so evaluate it only once.
        var scrutinee = LowerForValue(match.Scrutinee, output);
        if (scrutinee is not BoundVariableExpression)
            scrutinee = Spill(scrutinee, output);

        BoundStatement? chain = null;
        for (int i = match.Arms.Length - 1; i >= 0; i--)
        {
            var arm = match.Arms[i];
            var body = new List<BoundStatement>();
            bool isLast = chain is null;

            if (isLast && arm.Guard is null && (arm.Pattern is BoundBindingPattern || !arm.Pattern.HasBindings))
            {
                if (arm.Pattern is BoundBindingPattern binding)
                    body.Add(new BoundVariableDeclaration(binding.Variable, scrutinee));
                LowerArmBody(arm.Body, sink, body);
                chain = new BoundBlockStatement([.. body]);
                continue;
            }

            LowerArmBody(arm.Body, sink, body);
            chain = new BoundIfStatement(ArmCondition(scrutinee, arm), new BoundBlockStatement([.. body]), chain ?? new BoundUnreachableStatement());
        }

        if (chain is BoundBlockStatement block)
            output.AddRange(block.Statements);
        else if (chain is not null)
            output.Add(chain);
    }

    private BoundExpression ArmCondition(BoundExpression scrutinee, BoundMatchArm arm)
    {
        BoundExpression? condition = arm.Pattern is BoundWildcardPattern ? null : new BoundIsPatternExpression(scrutinee, arm.Pattern);
        if (arm.Guard is not null)
        {
            var guardPrelude = new List<BoundStatement>();
            var guard = LowerForValue(arm.Guard, guardPrelude);
            if (guardPrelude.Count > 0)
                throw new InvalidOperationException("The binder should have rejected a guard that needs statements.");
            condition = condition is null ? guard : new BoundBinaryExpression(condition, BoundBinaryOperatorKind.LogicalAnd, guard, TypeSymbol.Bool);
        }
        return condition ?? new BoundLiteralExpression(true, TypeSymbol.Bool);
    }

    private void LowerArmBody(BoundExpression body, Func<BoundExpression, BoundStatement>? sink, List<BoundStatement> output)
    {
        var statements = new List<BoundStatement>();
        if (sink is null)
            LowerForEffect(body, statements);
        else
            LowerInto(body, sink, statements);
        output.AddRange(Flatten(statements is [var only] ? only : new BoundBlockStatement([.. statements])));
    }

    // Null safety. Option and Result are emitted as structs with IsSome/IsOk, Value and Error members.

    /// <summary>
    /// `a ?? b` → `a.IsSome ? a.Value : b`, or, when b needs statements,
    /// `T t; if (a.IsSome) { t = a.Value; } else { …; t = b; }` so b still runs only when needed.
    /// </summary>
    private BoundExpression LowerNullCoalescing(BoundNullCoalescingExpression coalescing, List<BoundStatement> prelude)
    {
        var left = LowerOptionalOperand(coalescing.Left, prelude);
        // When the result is optional (`a ?? b` with b optional too) the left is used as it is.
        BoundExpression whenSome = coalescing.Type == left.Type ? left : Value(left);

        var rightPrelude = new List<BoundStatement>();
        var right = LowerForValue(coalescing.Right, rightPrelude);
        if (rightPrelude.Count == 0)
            return new BoundConditionalExpression(HasValue(left), whenSome, right, coalescing.Type);

        var temp = NewTemp(coalescing.Type);
        prelude.Add(new BoundVariableDeclaration(temp, null));
        rightPrelude.Add(Assign(temp, right));
        prelude.Add(new BoundIfStatement(
            HasValue(left),
            new BoundBlockStatement([Assign(temp, whenSome)]),
            new BoundBlockStatement([.. rightPrelude])));
        return new BoundVariableExpression(temp);
    }

    /// <summary>`a?.b.c()` → `a.IsSome ? Option&lt;T&gt;.Some(a.Value.b.c()) : Option&lt;T&gt;.None` (no Some when the chain is already optional).</summary>
    private BoundExpression LowerConditionalAccess(BoundConditionalAccessExpression access, List<BoundStatement> prelude)
    {
        var target = LowerOptionalOperand(access.Target, prelude);
        var chainPrelude = new List<BoundStatement>();
        BoundExpression chain = null!;
        WithReceiver(access, target, () => chain = LowerForValue(access.WhenNotNull, chainPrelude));

        var optional = (EnumSymbol)access.Type.OriginalDefinition;
        var whenSome = chain.Type == access.Type
            ? chain
            : new BoundVariantCreationExpression(optional.FindVariant("Some")!, [chain], access.Type);
        var whenNone = new BoundVariantCreationExpression(optional.FindVariant("None")!, [], access.Type);

        if (chainPrelude.Count == 0)
            return new BoundConditionalExpression(HasValue(target), whenSome, whenNone, access.Type);

        var temp = NewTemp(access.Type);
        prelude.Add(new BoundVariableDeclaration(temp, null));
        chainPrelude.Add(Assign(temp, whenSome));
        prelude.Add(new BoundIfStatement(
            HasValue(target),
            new BoundBlockStatement([.. chainPrelude]),
            new BoundBlockStatement([Assign(temp, whenNone)])));
        return new BoundVariableExpression(temp);
    }

    /// <summary>`f()?` → `T? t = f(); if (!t.IsSome) { return None; }` (or `return Err(t.Error)`), then `t.Value`.</summary>
    private BoundExpression LowerTry(BoundTryExpression attempt, List<BoundStatement> prelude)
    {
        var operand = LowerOptionalOperand(attempt.Operand, prelude);
        var returnType = attempt.FunctionReturnType;
        var returned = (EnumSymbol)returnType.OriginalDefinition;

        BoundExpression failure = operand.Type.IsOption
            ? new BoundVariantCreationExpression(returned.FindVariant("None")!, [], returnType)
            : new BoundVariantCreationExpression(
                returned.FindVariant("Err")!,
                [Widen(new BoundOptionalMemberExpression(operand, "Error", operand.Type.TypeArguments[1]), returnType.TypeArguments[1])],
                returnType);

        prelude.Add(new BoundIfStatement(Not(HasValue(operand)), new BoundReturnStatement(failure), null));
        return Value(operand);
    }

    /// <summary>Optional operands are tested and then read, so anything but a variable is evaluated once into a temporary.</summary>
    private BoundExpression LowerOptionalOperand(BoundExpression operand, List<BoundStatement> prelude)
    {
        var lowered = LowerForValue(operand, prelude);
        return lowered is BoundVariableExpression ? lowered : Spill(lowered, prelude);
    }

    private void WithReceiver(BoundConditionalAccessExpression access, BoundExpression target, Action lower)
    {
        _substitutions[access.Receiver] = Value(target);
        try
        {
            lower();
        }
        finally
        {
            _substitutions.Remove(access.Receiver);
        }
    }

    private static BoundExpression HasValue(BoundExpression optional) =>
        new BoundOptionalMemberExpression(optional, optional.Type.IsOption ? "IsSome" : "IsOk", TypeSymbol.Bool);

    private static BoundExpression Value(BoundExpression optional) =>
        new BoundOptionalMemberExpression(optional, "Value", optional.Type.TypeArguments[0]);

    /// <summary>`a &amp;&amp; { s; b }` →`bool t = a; if (t) { s; t = b; }` so the right side still runs only when needed.</summary>
    private BoundExpression LowerShortCircuit(BoundBinaryExpression logical, List<BoundStatement> prelude)
    {
        var left = LowerForValue(logical.Left, prelude);
        var rightPrelude = new List<BoundStatement>();
        var right = LowerForValue(logical.Right, rightPrelude);
        if (rightPrelude.Count == 0)
            return new BoundBinaryExpression(left, logical.Operator, right, logical.Type);

        var temp = NewTemp(TypeSymbol.Bool);
        prelude.Add(new BoundVariableDeclaration(temp, left));
        rightPrelude.Add(Assign(temp, right));
        BoundExpression test = new BoundVariableExpression(temp);
        if (logical.Operator == BoundBinaryOperatorKind.LogicalOr)
            test = Not(test);
        prelude.Add(new BoundIfStatement(test, new BoundBlockStatement([.. rightPrelude]), null));
        return new BoundVariableExpression(temp);
    }

    /// <summary>`x += { s; e }` reads x before s runs, so it becomes `T t = x; s; x = t + e;`.</summary>
    private BoundExpression LowerAssignment(BoundAssignmentExpression assignment, List<BoundStatement> prelude)
    {
        if (assignment.CompoundOperator is not { } op)
            return new BoundAssignmentExpression(assignment.Variable, null, LowerForValue(assignment.Value, prelude));

        var valuePrelude = new List<BoundStatement>();
        var value = LowerForValue(assignment.Value, valuePrelude);
        if (valuePrelude.Count == 0)
            return new BoundAssignmentExpression(assignment.Variable, op, value);

        var original = Spill(new BoundVariableExpression(assignment.Variable), prelude);
        prelude.AddRange(valuePrelude);
        var combined = new BoundBinaryExpression(original, op, value, assignment.Variable.Type);
        return new BoundAssignmentExpression(assignment.Variable, null, combined);
    }

    /// <summary>
    /// Lowers operands left to right. If a later operand needs prelude statements, earlier operands
    /// are first spilled into temporaries so they are still evaluated before those statements run.
    /// </summary>
    private ImmutableArray<BoundExpression> LowerOperands(ImmutableArray<BoundExpression> operands, List<BoundStatement> prelude)
    {
        var results = new List<BoundExpression>();
        foreach (var operand in operands)
        {
            var local = new List<BoundStatement>();
            var value = LowerForValue(operand, local);
            if (local.Count > 0)
            {
                for (int i = 0; i < results.Count; i++)
                {
                    if (!IsInvariant(results[i]))
                        results[i] = Spill(results[i], prelude);
                }
                prelude.AddRange(local);
            }
            results.Add(value);
        }
        return [.. results];
    }

    /// <summary>Lowers <paramref name="expression"/> and succeeds only if that needed no prelude statements.</summary>
    private bool TryLowerPure(BoundExpression expression, out BoundExpression lowered)
    {
        var scratch = new List<BoundStatement>();
        lowered = LowerForValue(expression, scratch);
        return scratch.Count == 0;
    }

    // Helpers

    private static VariableSymbol NewTemp(TypeSymbol type) =>
        new("temp", type, isMutable: true, VariableKind.Temporary);

    private static BoundVariableExpression Spill(BoundExpression value, List<BoundStatement> prelude)
    {
        var temp = NewTemp(value.Type);
        prelude.Add(new BoundVariableDeclaration(temp, value));
        return new BoundVariableExpression(temp);
    }

    private static bool IsConstant(BoundExpression expression) => expression switch
    {
        BoundLiteralExpression => true,
        BoundConversionExpression conversion => IsConstant(conversion.Expression),
        _ => false,
    };

    /// <summary>Constants and immutable variables cannot change while a loop runs.</summary>
    private static bool IsInvariant(BoundExpression expression) => expression switch
    {
        BoundVariableExpression variable => !variable.Variable.IsMutable,
        BoundConversionExpression conversion => IsInvariant(conversion.Expression),
        _ => IsConstant(expression),
    };

    private static BoundExpressionStatement Assign(VariableSymbol variable, BoundExpression value) =>
        new(new BoundAssignmentExpression(variable, null, value));

    private static BoundExpression Widen(BoundExpression expression, TypeSymbol type) =>
        expression.Type == type ? expression : new BoundConversionExpression(type, expression);

    private static BoundUnaryExpression Not(BoundExpression operand) =>
        new(BoundUnaryOperatorKind.LogicalNot, operand, TypeSymbol.Bool);

    private static IEnumerable<BoundStatement> Flatten(BoundStatement statement) =>
        statement is BoundBlockStatement block ? block.Statements : [statement];
}
