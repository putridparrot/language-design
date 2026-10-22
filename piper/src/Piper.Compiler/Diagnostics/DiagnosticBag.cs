using System.Collections;
using Piper.Binding;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Diagnostics;

/// <summary>
/// Collects diagnostics from every compiler phase. Each kind of error has its own Report method
/// so codes and wording stay consistent. Codes: PIP00xx lexer, PIP01xx parser, PIP02xx binder.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public int Count => _diagnostics.Count;

    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Error(string code, TextSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, span, message));

    private void Warning(string code, TextSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Warning, span, message));

    // Lexer

    public void ReportUnexpectedCharacter(TextSpan span, string text) =>
        Error("PIP0001", span, $"Unexpected character '{text}'.");

    public void ReportUnterminatedString(TextSpan span) =>
        Error("PIP0002", span, "Unterminated string literal.");

    public void ReportInvalidCharacterLiteral(TextSpan span) =>
        Error("PIP0003", span, "A character literal must contain exactly one character, e.g. 'a' or '\\n'.");

    public void ReportInvalidEscapeSequence(TextSpan span, string text) =>
        Error("PIP0004", span, $"Invalid escape sequence '{text}'.");

    public void ReportInvalidNumber(TextSpan span, string text) =>
        Error("PIP0005", span, $"Invalid numeric literal '{text}'.");

    public void ReportUnterminatedComment(TextSpan span) =>
        Error("PIP0006", span, "Unterminated block comment.");

    public void ReportIntegerTooLarge(TextSpan span, string text, string typeName) =>
        Error("PIP0007", span, typeName == "Int"
            ? $"Integer literal '{text}' is too large for Int; add an 'L' suffix to make it a Long."
            : $"Integer literal '{text}' is too large for {typeName}.");

    public void ReportUnterminatedInterpolation(TextSpan span) =>
        Error("PIP0008", span, "Unterminated interpolation; expected '}'. Use '\\{' for a literal brace.");

    public void ReportEmptyInterpolation(TextSpan span) =>
        Error("PIP0009", span, "Empty interpolation '{}'. Use '\\{' for a literal brace.");

    // Parser

    public void ReportExpectedToken(TextSpan span, TokenKind expected, Token actual) =>
        Error("PIP0100", span, $"Expected {SyntaxFacts.Describe(expected)} but found {Describe(actual)}.");

    public void ReportExpectedExpression(TextSpan span, Token actual) =>
        Error("PIP0101", span, $"Expected an expression but found {Describe(actual)}.");

    public void ReportExpectedDeclaration(TextSpan span, Token actual) =>
        Error("PIP0102", span, $"Expected 'fn', 'record' or 'enum' but found {Describe(actual)}.");

    public void ReportExpectedType(TextSpan span, Token actual) =>
        Error("PIP0103", span, $"Expected a type but found {Describe(actual)}.");

    public void ReportUnexpectedTokenInInterpolation(TextSpan span, Token actual) =>
        Error("PIP0104", span, $"Unexpected {Describe(actual)} in interpolation.");

    public void ReportExpectedPattern(TextSpan span, Token actual) =>
        Error("PIP0105", span, $"Expected a pattern but found {Describe(actual)}.");

    private static string Describe(Token token) => token.Kind switch
    {
        TokenKind.EndOfFile => "end of file",
        TokenKind.Identifier or TokenKind.IntegerLiteral or TokenKind.DoubleLiteral => $"'{token.Text}'",
        _ when token.Text.Length > 0 && token.Text.Length <= 20 => $"'{token.Text}'",
        _ => SyntaxFacts.Describe(token.Kind),
    };

    // Binder

    public void ReportUndefinedName(TextSpan span, string name) =>
        Error("PIP0200", span, $"The name '{name}' does not exist in the current scope.");

    public void ReportDuplicateDeclaration(TextSpan span, string name) =>
        Error("PIP0201", span, $"'{name}' is already declared in this scope.");

    public void ReportCannotConvert(TextSpan span, TypeSymbol from, TypeSymbol to) =>
        Error("PIP0202", span, $"Cannot convert type '{from}' to '{to}'.");

    public void ReportUndefinedBinaryOperator(TextSpan span, string op, TypeSymbol left, TypeSymbol right) =>
        Error("PIP0203", span, $"Operator '{op}' is not defined for types '{left}' and '{right}'.");

    public void ReportUndefinedUnaryOperator(TextSpan span, string op, TypeSymbol operand) =>
        Error("PIP0204", span, $"Operator '{op}' is not defined for type '{operand}'.");

    public void ReportWrongArgumentCount(TextSpan span, string name, int expected, int actual) =>
        Error("PIP0205", span, $"'{name}' expects {expected} argument{(expected == 1 ? "" : "s")} but was given {actual}.");

    /// <param name="what">A quoted name such as <c>'x'</c>, or "This expression".</param>
    public void ReportNotCallable(TextSpan span, string what) =>
        Error("PIP0206", span, $"{what} is not a function or record and cannot be called.");

    public void ReportCannotAssignImmutable(TextSpan span, VariableSymbol variable) =>
        Error("PIP0207", span, variable.Kind switch
        {
            VariableKind.Parameter => $"Cannot assign to parameter '{variable.Name}'; parameters are immutable.",
            VariableKind.LoopVariable => $"Cannot assign to loop variable '{variable.Name}'.",
            VariableKind.PatternVariable => $"Cannot assign to pattern variable '{variable.Name}'.",
            _ => $"Cannot assign to '{variable.Name}' because it is immutable; declare it with 'var' instead of 'let'.",
        });

    public void ReportInvalidAssignmentTarget(TextSpan span) =>
        Error("PIP0208", span, "The left-hand side of an assignment must be a variable.");

    public void ReportImmutableField(TextSpan span, string field) =>
        Error("PIP0208", span, $"Cannot assign to field '{field}'; record fields are immutable.");

    public void ReportNotAllPathsReturn(TextSpan span, string function) =>
        Error("PIP0209", span, $"Not all code paths in '{function}' return a value.");

    public void ReportConditionNotBool(TextSpan span, TypeSymbol actual) =>
        Error("PIP0210", span, $"A condition must be of type 'Bool' but is '{actual}'.");

    public void ReportUndefinedType(TextSpan span, string name) =>
        Error("PIP0211", span, $"The type '{name}' does not exist.");

    public void ReportUnknownMember(TextSpan span, TypeSymbol type, string member) =>
        Error("PIP0212", span, $"Type '{type}' has no member '{member}'.");

    public void ReportOutsideLoop(TextSpan span, string keyword) =>
        Error("PIP0213", span, $"'{keyword}' can only be used inside a loop.");

    public void ReportExpressionHasNoValue(TextSpan span) =>
        Error("PIP0214", span, "This expression does not produce a value.");

    public void ReportInvalidUnitUsage(TextSpan span, string what) =>
        Error("PIP0214", span, $"A {what} cannot be of type 'Unit'.");

    public void ReportUnexpectedReturnValue(TextSpan span, string function) =>
        Error("PIP0215", span, $"'{function}' returns Unit, so 'return' cannot have a value.");

    public void ReportMissingReturnValue(TextSpan span, string function, TypeSymbol type) =>
        Error("PIP0215", span, $"'{function}' must return a value of type '{type}'.");

    public void ReportRangeOutsideFor(TextSpan span) =>
        Error("PIP0216", span, "Ranges can currently only be used in 'for' loops.");

    public void ReportForRequiresRange(TextSpan span) =>
        Error("PIP0216", span, "A 'for' loop must iterate over a range, e.g. 'for i in 0..10'.");

    public void ReportNotAValue(TextSpan span, string name, string kind) =>
        Error("PIP0217", span, $"'{name}' is a {kind}, not a value.");

    public void ReportInvalidMain(TextSpan span) =>
        Error("PIP0219", span, "'main' must take no parameters and return Unit.");

    public void ReportFieldNamedAsRecord(TextSpan span, string name) =>
        Error("PIP0220", span, $"A field cannot have the same name as its record '{name}'.");

    public void ReportIncompatibleBranches(TextSpan span, TypeSymbol thenType, TypeSymbol elseType) =>
        Error("PIP0221", span, $"The 'if' and 'else' branches have incompatible types '{thenType}' and '{elseType}'.");

    public void ReportIncompatibleArms(TextSpan span, TypeSymbol expected, TypeSymbol actual) =>
        Error("PIP0221", span, $"This match arm has type '{actual}', which is incompatible with the earlier arms' type '{expected}'.");

    public void ReportReservedMemberName(TextSpan span, string name) =>
        Error("PIP0222", span, $"'{name}' is reserved (it clashes with a member every record has) and cannot be used here.");

    // Enums and pattern matching

    public void ReportNonExhaustiveMatch(TextSpan span, string missing) =>
        Error("PIP0230", span, $"The match is not exhaustive; '{missing}' is not covered.");

    public void ReportUnreachableArm(TextSpan span) =>
        Warning("PIP0231", span, "This match arm is unreachable; earlier arms already cover every value it matches.");

    public void ReportVariantRequiresArguments(TextSpan span, string variant, string fields) =>
        Error("PIP0232", span, $"Variant '{variant}' has fields and must be constructed with arguments: {variant}({fields}).");

    public void ReportVariantHasNoFields(TextSpan span, string variant) =>
        Error("PIP0232", span, $"Variant '{variant}' has no fields; write '{variant}' without parentheses.");

    public void ReportGuardContainsStatements(TextSpan span) =>
        Error("PIP0233", span, "A match guard cannot (yet) contain blocks with statements or match expressions.");

    public void ReportPatternTypeMismatch(TextSpan span, string pattern, TypeSymbol expected) =>
        Error("PIP0234", span, $"{pattern} cannot match a value of type '{expected}'.");

    public void ReportUnknownVariant(TextSpan span, TypeSymbol enumType, string name) =>
        Error("PIP0235", span, $"Enum '{enumType}' has no variant '{name}'.");

    public void ReportUnknownConstructorPattern(TextSpan span, string name, TypeSymbol expected) =>
        Error("PIP0235", span, $"'{name}' is neither a variant of '{expected}' nor a record.");

    public void ReportPatternArity(TextSpan span, string name, int expected, int actual) =>
        Error("PIP0236", span, $"'{name}' has {expected} field{(expected == 1 ? "" : "s")} but the pattern has {actual}.");

    public void ReportBindingInOrPattern(TextSpan span) =>
        Error("PIP0237", span, "Variables cannot (yet) be bound inside '|' patterns.");

    public void ReportEmptyEnum(TextSpan span, string name) =>
        Error("PIP0238", span, $"Enum '{name}' must have at least one variant.");

    public void ReportVariantNamedAsEnum(TextSpan span, string name) =>
        Error("PIP0238", span, $"A variant cannot have the same name as its enum '{name}'.");

    public void ReportInvalidRangePattern(TextSpan span, TypeSymbol type) =>
        Error("PIP0239", span, $"Range patterns need Int, Long or Char bounds, not '{type}'.");

    public void ReportEmptyRangePattern(TextSpan span) =>
        Error("PIP0239", span, "This range pattern is empty: its lower bound is not below its upper bound.");
}
