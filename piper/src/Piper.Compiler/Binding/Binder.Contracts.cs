using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

public sealed partial class Binder
{
    /// <summary>
    /// Binds every <c>requires</c> clause: functions' and methods' preconditions, in the scope their bodies see, and
    /// records' invariants, over their fields. This runs after all signatures and before any body, so a call can be
    /// checked against its callee's conditions whatever order they're declared in.
    /// </summary>
    private void BindContracts(IEnumerable<TypeSymbol> types, IEnumerable<FunctionInfo> functions)
    {
        foreach (var info in functions)
        {
            if (info.Syntax.Requires.IsEmpty)
                continue;
            InFile(info.File);
            if (info.Symbol is MethodSymbol { Trait: not null })
            {
                _diagnostics.ReportInvalidRequires(info.Syntax.Requires[0].Span, "Trait methods can't have 'requires' clauses yet; put the check in the implementations.");
                continue;
            }
            var scope = new Scope(info.SignatureScope);
            if (info.Symbol is MethodSymbol { This: { } @this })
                scope.TryDeclare(@this);
            foreach (var parameter in info.Symbol.Parameters)
                scope.TryDeclare(parameter);
            info.Symbol.Preconditions = BindConditions(info.Syntax.Requires, scope, info.Symbol.Parameters);
        }

        foreach (var record in types.OfType<RecordSymbol>().Where(r => !r.Declaration.Requires.IsEmpty))
        {
            InFile(FileOf(record));
            var scope = new Scope(TypeParameterScope(record, _file));
            var fields = record.Fields.Select(f => new VariableSymbol(f.Name, f.Type, isMutable: false, VariableKind.Parameter)).ToImmutableArray();
            foreach (var field in fields)
                scope.TryDeclare(field);
            record.InvariantFields = fields;
            record.Invariants = BindConditions(record.Declaration.Requires, scope, fields);
        }

        // Required trait methods have no body, so they aren't among the functions above.
        foreach (var trait in types.OfType<TraitSymbol>())
        {
            InFile(FileOf(trait));
            foreach (var method in trait.Declaration.Methods.Where(m => !m.HasBody && !m.Requires.IsEmpty))
                _diagnostics.ReportInvalidRequires(method.Requires[0].Span, "Trait methods can't have 'requires' clauses yet; put the check in the implementations.");
        }
        _scope = _file.Scope;
    }

    private ImmutableArray<BoundContract> BindConditions(ImmutableArray<ExpressionSyntax> conditions, Scope scope, ImmutableArray<VariableSymbol> variables)
    {
        var outer = _scope;
        _scope = scope;
        var contracts = ImmutableArray.CreateBuilder<BoundContract>();
        foreach (var syntax in conditions)
        {
            // Like an if's condition, it can bind with `is` (`i is Some(v) && v > 0`), in a scope of its own.
            _scope = new Scope(scope);
            var condition = BindCondition(syntax, allowPatternBindings: true);
            var text = _file.Source.ToString(syntax.Span);
            // The values worth showing when it fails: those of the variables it mentions that print as their value.
            var mentioned = variables
                .Where(v => PrintsValue(v.Type) && Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(v.Name)}\b"))
                .ToImmutableArray();
            contracts.Add(new BoundContract(condition, text, mentioned, syntax.Span));
        }
        _scope = outer;
        return contracts.ToImmutable();
    }

    /// <summary>Builtin types, Piper records and enums, tuples and optionals print their values; .NET types like List print their type name.</summary>
    private static bool PrintsValue(TypeSymbol type) =>
        TypeSymbol.Builtins.Contains(type) || type.OriginalDefinition is NamedTypeDefinition ||
        (type is TupleTypeSymbol tuple && tuple.ElementTypes.All(PrintsValue)) || (type.IsOption && PrintsValue(type.TypeArguments[0]));

    /// <summary>
    /// A compile-time error for a call or construction whose arguments are constants that break one of the callee's
    /// conditions: <c>Sqrt(-1.0)</c>, <c>Percentage(150)</c>. Conditions that depend on anything else are left to run time.
    /// </summary>
    private void CheckContracts(
        ImmutableArray<BoundContract> contracts, IReadOnlyList<VariableSymbol> variables, IReadOnlyList<BoundExpression> arguments,
        TextSpan span, string owner, string kind)
    {
        if (contracts.IsEmpty)
            return;
        var values = new Dictionary<VariableSymbol, object>();
        for (int i = 0; i < variables.Count && i < arguments.Count; i++)
        {
            if (ConstantFolding.Evaluate(arguments[i], values) is { } value)
                values[variables[i]] = value;
        }
        foreach (var contract in contracts)
        {
            if (ConstantFolding.Evaluate(contract.Condition, values) is false)
                _diagnostics.ReportContractBroken(span, owner, kind, contract.Text);
        }
    }

    /// <summary>
    /// The statements that check a function's preconditions when it's called, put before its body: one call to the
    /// internal Require builtin per condition, with the condition's text and the values of the parameters it mentions.
    /// </summary>
    private static IEnumerable<BoundStatement> PreconditionChecks(FunctionSymbol function) =>
        // Each check maps to its condition's line, so a failure's stack trace points at the `requires`.
        function.Preconditions.Select(contract => (BoundStatement)new BoundExpressionStatement(new BoundCallExpression(
            BuiltinFunctions.Require,
            [],
            [
                contract.Condition,
                new BoundLiteralExpression(contract.Text, TypeSymbol.String),
                ContractValues(contract),
                new BoundLiteralExpression(contract.Mentioned is [var only] ? only.Name : "", TypeSymbol.String),
            ],
            TypeSymbol.Unit)) { SourceSpan = contract.Span });

    /// <summary><c>"x = {x}, y = {y}"</c>: the mentioned variables' values, as an interpolated string.</summary>
    internal static BoundExpression ContractValues(BoundContract contract)
    {
        var parts = new List<BoundExpression>();
        foreach (var variable in contract.Mentioned)
        {
            parts.Add(new BoundLiteralExpression((parts.Count == 0 ? "" : ", ") + variable.Name + " = ", TypeSymbol.String));
            parts.Add(new BoundVariableExpression(variable));
        }
        return parts.Count == 0 ? new BoundLiteralExpression("", TypeSymbol.String) : new BoundInterpolatedStringExpression([.. parts]);
    }
}
