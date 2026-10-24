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

    /// <summary>The file that spans in newly reported diagnostics refer to; set by whichever phase is processing a file.</summary>
    public SourceText? CurrentSource { get; set; }

    private void Error(string code, TextSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, span, message) { Source = CurrentSource });

    private void Warning(string code, TextSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Warning, span, message) { Source = CurrentSource });

    // Lexer

    public void ReportUnexpectedCharacter(TextSpan span, string text) =>
        Error("PIP0001", span, $"Unexpected character '{text}'.");

    public void ReportUnterminatedString(TextSpan span) =>
        Error("PIP0002", span, "Unterminated string literal.");

    public void ReportRawStringLayout(TextSpan span, string message) =>
        Error("PIP0010", span, message);

    public void ReportInvalidCharacterLiteral(TextSpan span) =>
        Error("PIP0003", span, "A character literal must contain exactly one character, e.g. 'a' or '\\n'.");

    public void ReportInvalidEscapeSequence(TextSpan span, string text) =>
        Error("PIP0004", span, $"Invalid escape sequence '{text}'.");

    public void ReportInvalidNumber(TextSpan span, string text) =>
        Error("PIP0005", span, $"Invalid numeric literal '{text}'.");

    public void ReportUnterminatedComment(TextSpan span) =>
        Error("PIP0006", span, "Unterminated block comment.");

    public void ReportIntegerTooLarge(TextSpan span, string text, string typeName) =>
        Error("PIP0007", span, typeName == "int"
            ? $"Integer literal '{text}' is too large for int; add an 'L' suffix to make it a long."
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
        Error("PIP0102", span, $"Expected 'fn', 'record', 'enum', 'trait' or 'extend' but found {Describe(actual)}.");

    public void ReportExpectedType(TextSpan span, Token actual) =>
        Error("PIP0103", span, $"Expected a type but found {Describe(actual)}.");

    public void ReportUnexpectedTokenInInterpolation(TextSpan span, Token actual) =>
        Error("PIP0104", span, $"Unexpected {Describe(actual)} in interpolation.");

    public void ReportMisplacedDirective(TextSpan span, string directive) =>
        Error("PIP0106", span, directive == "namespace"
            ? "A file can have one 'namespace' declaration, before any other declarations."
            : "'using' directives must come before the file's declarations.");

    public void ReportExpectedPattern(TextSpan span, Token actual) =>
        Error("PIP0105", span, $"Expected a pattern but found {Describe(actual)}.");

    public void ReportUsingDeconstruction(TextSpan span) =>
        Error("PIP0108", span, "A 'using' declaration declares a single variable; it can't deconstruct.");

    public void ReportTupleTooShort(TextSpan span) =>
        Error("PIP0107", span, "A tuple needs at least two elements; for a function type, add a return type, e.g. '(int) => bool'.");

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
            VariableKind.This => "Cannot assign to 'this'.",
            _ => $"Cannot assign to '{variable.Name}' because it is immutable; declare it with 'var' instead of 'let'.",
        });

    public void ReportInvalidAssignmentTarget(TextSpan span) =>
        Error("PIP0208", span, "The left-hand side of an assignment must be a variable.");

    public void ReportImmutableField(TextSpan span, string field) =>
        Error("PIP0208", span, $"Cannot assign to field '{field}'; record fields are immutable.");

    public void ReportNotAllPathsReturn(TextSpan span, string function) =>
        Error("PIP0209", span, $"Not all code paths in '{function}' return a value.");

    public void ReportConditionNotBool(TextSpan span, TypeSymbol actual) =>
        Error("PIP0210", span, $"A condition must be of type 'bool' but is '{actual}'.");

    public void ReportUndefinedType(TextSpan span, string name) =>
        Error("PIP0211", span, $"The type '{name}' does not exist.");

    public void ReportUnknownMember(TextSpan span, TypeSymbol type, string member) =>
        Error("PIP0212", span, type.IsOption
            ? $"Type '{type}' has no member '{member}'; use '?.{member}' to reach the value inside the optional."
            : $"Type '{type}' has no member '{member}'.");

    public void ReportOutsideLoop(TextSpan span, string keyword) =>
        Error("PIP0213", span, $"'{keyword}' can only be used inside a loop.");

    public void ReportExpressionHasNoValue(TextSpan span) =>
        Error("PIP0214", span, "This expression does not produce a value.");

    public void ReportInvalidUnitUsage(TextSpan span, string what) =>
        Error("PIP0214", span, $"A {what} cannot be of type 'unit'.");

    public void ReportUnexpectedReturnValue(TextSpan span, string function) =>
        Error("PIP0215", span, $"'{function}' returns unit, so 'return' cannot have a value.");

    public void ReportMissingReturnValue(TextSpan span, string function, TypeSymbol type) =>
        Error("PIP0215", span, $"'{function}' must return a value of type '{type}'.");

    public void ReportRangeOutsideFor(TextSpan span) =>
        Error("PIP0216", span, "Ranges can currently only be used in 'for' loops.");

    public void ReportForRequiresRange(TextSpan span) =>
        Error("PIP0216", span, "A 'for' loop must iterate over a range, e.g. 'for i in 0..10'.");

    public void ReportNotAValue(TextSpan span, string name, string kind) =>
        Error("PIP0217", span, $"'{name}' is a {kind}, not a value.");

    public void ReportInvalidMain(TextSpan span) =>
        Error("PIP0219", span, "'main' must take no parameters and return unit.");

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

    public void ReportPatternTypeMismatch(TextSpan span, string pattern, TypeSymbol expected) =>
        Error("PIP0234", span, $"{pattern} cannot match a value of type '{expected}'.");

    public void ReportUnknownVariant(TextSpan span, TypeSymbol enumType, string name) =>
        Error("PIP0235", span, $"Enum '{enumType}' has no variant '{name}'.");

    public void ReportUnknownConstructorPattern(TextSpan span, string name, TypeSymbol expected) =>
        Error("PIP0235", span, $"'{name}' is neither a variant of '{expected}' nor a record.");

    public void ReportPatternArity(TextSpan span, string name, int expected, int actual) =>
        Error("PIP0236", span, $"'{name}' has {expected} field{(expected == 1 ? "" : "s")} but the pattern has {actual}.");

    public void ReportBindingInOrPattern(TextSpan span) =>
        Error("PIP0237", span, "Variables can only be bound inside '|' patterns in a match arm, not in an 'is' test.");

    public void ReportEmptyEnum(TextSpan span, string name) =>
        Error("PIP0238", span, $"Enum '{name}' must have at least one variant.");

    public void ReportVariantNamedAsEnum(TextSpan span, string name) =>
        Error("PIP0238", span, $"A variant cannot have the same name as its enum '{name}'.");

    public void ReportInvalidRangePattern(TextSpan span, TypeSymbol type) =>
        Error("PIP0239", span, $"Range patterns need integer or char bounds, not '{type}'.");

    // Generics, traits and methods

    public void ReportCannotInferTypeArgument(TextSpan span, string parameter, string callee) =>
        Error("PIP0240", span, $"Cannot infer type argument '{parameter}' for '{callee}'; write the type arguments, e.g. '{callee}<int>', or add a type annotation, e.g. 'let x: Option<int> = ...'.");

    public void ReportNotGeneric(TextSpan span, string name) =>
        Error("PIP0241", span, $"'{name}' is not generic and takes no type arguments.");

    public void ReportTypeArgumentCount(TextSpan span, string name, int expected, int actual) =>
        Error("PIP0241", span, $"'{name}' takes {expected} type argument{(expected == 1 ? "" : "s")} but was given {actual}.");

    public void ReportConstraintNotSatisfied(TextSpan span, TypeSymbol argument, TypeSymbol trait, TypeParameterSymbol parameter) =>
        Error("PIP0242", span, $"'{argument}' does not implement '{trait}', which type parameter '{parameter}' requires.");

    public void ReportUnknownMethod(TextSpan span, TypeSymbol type, string name) =>
        Error("PIP0243", span, type.IsOption
            ? $"Type '{type}' has no method '{name}'; use '?.{name}(...)' to call it on the value inside the optional."
            : $"Type '{type}' has no method '{name}'.");

    public void ReportThisOutsideInstanceMethod(TextSpan span) =>
        Error("PIP0244", span, "'this' can only be used inside instance (non-static) methods of a trait or extend block.");

    public void ReportStaticOutsideExtend(TextSpan span) =>
        Error("PIP0244", span, "'static' can only be used on methods in an extend block; top-level functions are already static.");

    public void ReportStaticMethodOnInstance(TextSpan span, string method, TypeSymbol type) =>
        Error("PIP0244", span, $"'{method}' is static; call it on the type: {type.OriginalDefinition}.{method}(...).");

    public void ReportInstanceMethodCalledStatically(TextSpan span, string method) =>
        Error("PIP0244", span, $"'{method}' is an instance method; call it on a value: value.{method}(...).");

    public void ReportMissingTraitMethod(TextSpan span, TypeSymbol type, string method, TypeSymbol trait) =>
        Error("PIP0245", span, $"'{type}' does not implement '{method}', which trait '{trait}' requires.");

    public void ReportTraitSignatureMismatch(TextSpan span, string method, TypeSymbol trait, string expected) =>
        Error("PIP0246", span, $"'{method}' does not match its declaration in trait '{trait}': expected {expected}.");

    public void ReportInvalidExtendTarget(TextSpan span, string reason) =>
        Error("PIP0247", span, reason);

    public void ReportNotATrait(TextSpan span, string name) =>
        Error("PIP0248", span, $"'{name}' is not a trait.");

    public void ReportInvalidTraitMethod(TextSpan span, string reason) =>
        Error("PIP0250", span, reason);

    public void ReportMissingBody(TextSpan span, string name) =>
        Error("PIP0251", span, $"'{name}' needs a body; only trait methods can be declared without one.");

    public void ReportExistingClrMember(TextSpan span, TypeSymbol type, string name) =>
        Error("PIP0252", span, $"'{type}' already has a member named '{name}', which would hide this method.");

    // Null safety

    public void ReportNullWithoutOptionalType(TextSpan span) =>
        Error("PIP0260", span, "'null' needs an optional type here, e.g. 'let x: int? = null;'. Piper has no null references; 'null' means None.");

    public void ReportRequiresOptional(TextSpan span, string op, TypeSymbol type) =>
        Error("PIP0261", span, op == "?."
            ? $"'?.' needs an optional value but '{type}' is not optional; use '.' instead."
            : $"The left side of '??' must be optional but '{type}' is not.");

    public void ReportIsBindingOutsideCondition(TextSpan span) =>
        Error("PIP0262", span, "Variables bound by 'is' can only be used in an if or while condition (optionally joined with '&&').");

    public void ReportTryOutsideOptionalFunction(TextSpan span, string kind) =>
        Error("PIP0263", span, kind == "Option"
            ? "'?' on an optional value can only be used in a function that returns an optional value."
            : "'?' on a Result can only be used in a function that returns a Result.");

    public void ReportTryOperandNotOptional(TextSpan span, TypeSymbol type) =>
        Error("PIP0263", span, $"'?' needs an optional value or a Result, not '{type}'.");

    public void ReportBuiltinTypeRedeclared(TextSpan span, string name) =>
        Error("PIP0264", span, $"'{name}' is built into Piper and cannot be redeclared.");

    // Namespaces, visibility and .NET interop

    public void ReportPrivateTypeExposed(TextSpan span, string declaration, string privateType) =>
        Error("PIP0270", span, $"'{declaration}' is public but uses private type '{privateType}'; make '{declaration}' private too, or make '{privateType}' public.");

    public void ReportPrivateDeclaration(TextSpan span, string name, string file) =>
        Error("PIP0271", span, $"'{name}' is private to {Path.GetFileName(file)}.");

    public void ReportUnknownNamespace(TextSpan span, string name) =>
        Error("PIP0272", span, $"The namespace '{name}' does not exist (in Piper code or the .NET libraries).");

    public void ReportNotInNamespace(TextSpan span, string name, string @namespace) =>
        Error("PIP0272", span, $"The namespace '{@namespace}' has no member '{name}'.");

    public void ReportAmbiguousImport(TextSpan span, string name, IReadOnlyList<string> namespaces) =>
        Error("PIP0273", span, $"'{name}' is ambiguous: it is imported from {string.Join(" and ", namespaces.Select(n => $"'{n}'"))}. Qualify it, e.g. {namespaces[0]}.{name}.");

    public void ReportNoApplicableOverload(TextSpan span, string member, IEnumerable<TypeSymbol> arguments) =>
        Error("PIP0274", span, $"No overload of '{member}' accepts arguments ({string.Join(", ", arguments)}).");

    public void ReportAmbiguousOverload(TextSpan span, string member, IEnumerable<string> candidates) =>
        Error("PIP0275", span, $"The call to '{member}' is ambiguous between: {string.Join("; ", candidates)}.");

    public void ReportDuplicateMain(TextSpan span) =>
        Error("PIP0276", span, "A program can only have one 'main' function.");

    public void ReportNotAType(TextSpan span, string name, string kind) =>
        Error("PIP0277", span, $"'{name}' is a {kind}, not a type.");

    public void ReportCannotConstruct(TextSpan span, TypeSymbol type) =>
        Error("PIP0278", span, $"'{type}' has no public constructors.");

    // Lambdas, collections and exceptions

    public void ReportCannotInferLambda(TextSpan span) =>
        Error("PIP0280", span, "Can't infer this lambda's parameter types; annotate them, e.g. '(x: int) => …', or use it where a function type is expected.");

    public void ReportLambdaParameterCount(TextSpan span, TypeSymbol delegateType, int expected, int actual) =>
        Error("PIP0281", span, $"A '{delegateType}' takes {expected} parameter{(expected == 1 ? "" : "s")} but the lambda has {actual}.");

    public void ReportLambdaParameterType(TextSpan span, string name, TypeSymbol expected, TypeSymbol actual) =>
        Error("PIP0281", span, $"Lambda parameter '{name}' is declared as '{actual}' but must be '{expected}' here.");

    public void ReportNotIterable(TextSpan span, TypeSymbol type) =>
        Error("PIP0282", span, $"A 'for' loop needs a range (0..10) or a collection, but '{type}' is neither.");

    public void ReportNotAnException(TextSpan span, TypeSymbol type) =>
        Error("PIP0283", span, $"'{type}' is not an exception type (it doesn't derive from System.Exception).");

    public void ReportJumpOutOfFinally(TextSpan span, string keyword) =>
        Error("PIP0284", span, $"'{keyword}' cannot leave a finally or defer block.");

    public void ReportNotIndexable(TextSpan span, TypeSymbol type) =>
        Error("PIP0285", span, $"'{type}' cannot be indexed.");

    public void ReportReadOnlyMember(TextSpan span, string member) =>
        Error("PIP0285", span, $"'{member}' cannot be assigned; it has no public setter.");

    public void ReportUnreachableCatch(TextSpan span) =>
        Error("PIP0287", span, "This catch clause is unreachable: an earlier clause already catches its exceptions.");

    public void ReportCannotInferCollection(TextSpan span) =>
        Error("PIP0286", span, "Can't infer the element type of an empty collection; add a type annotation, e.g. 'let xs: List<int> = [];'.");

    public void ReportArrayOfNullDefaults(TextSpan span, TypeSymbol element) =>
        Error("PIP0288", span, $"An array of '{element}' can't be created empty, as its elements would be null; use a collection literal ([…]), Enumerable.Repeat(value, n).ToArray(), or an array of '{element}?'.");

    public void ReportCannotDeconstruct(TextSpan span, TypeSymbol type, int count) =>
        Error("PIP0289", span, $"A '{type}' can't be deconstructed into {count} parts; tuples, records and .NET types with a matching Deconstruct method can.");

    public void ReportDuplicateTupleName(TextSpan span, string name) =>
        Error("PIP0290", span, $"Tuple element name '{name}' is used more than once.");

    public void ReportReservedTupleName(TextSpan span, string name) =>
        Error("PIP0290", span, $"'{name}' can't name a tuple element: Item1, Item2, … are reserved for element positions.");

    public void ReportMisplacedOut(TextSpan span) =>
        Error("PIP0291", span, "'out' can only pass a variable to a .NET method's out parameter; Piper functions return several values as a tuple instead.");

    public void ReportMisplacedRef(TextSpan span) =>
        Error("PIP0311", span, "'ref' can only pass a variable to a .NET method's ref parameter.");

    public void ReportInvalidRefTarget(TextSpan span) =>
        Error("PIP0312", span, "'ref' needs a variable declared with 'var', or an element of an array, since .NET may write to it.");

    public void ReportEventMisused(TextSpan span, string name) =>
        Error("PIP0313", span, $"'{name}' is an event: add a handler with '+=' or remove one with '-='.");

    public void ReportTraitImplementedTwice(TextSpan span, TypeSymbol type, TypeSymbol first, TypeSymbol second) =>
        Error("PIP0314", span, $"'{type}' already implements '{first}', so it can't implement '{second}' too: a type implements each generic trait once, since its methods can't be overloaded.");

    public void ReportTraitInheritsItself(TextSpan span, string trait) =>
        Error("PIP0315", span, $"Trait '{trait}' inherits from itself.");

    public void ReportTraitMethodRedeclared(TextSpan span, string method, TypeSymbol parent) =>
        Error("PIP0316", span, $"'{method}' is already declared by '{parent}', which this trait inherits; a trait can't redeclare or override an inherited method.");

    public void ReportTraitMethodInheritedTwice(TextSpan span, string method, TypeSymbol first, TypeSymbol second) =>
        Error("PIP0317", span, $"This trait inherits '{method}' from both '{first}' and '{second}', so calls to it would be ambiguous.");

    public void ReportUnsupportedInterface(TextSpan span, TypeSymbol @interface) =>
        Error("PIP0318", span, $"'{@interface}' can't be implemented in Piper: only interfaces whose members are all instance methods, without ref or out parameters or type parameters of their own, can.");

    public void ReportStaticTraitAsType(TextSpan span, TypeSymbol trait) =>
        Error("PIP0319", span, $"'{trait}' has static methods, so it can only be a bound ('T: {trait}'), not the type of a value.");

    public void ReportStaticTraitMethodCall(TextSpan span, string method, TypeSymbol type) =>
        Error("PIP0320", span, $"'{type}' doesn't implement static method '{method}' itself; call the trait's default through a type parameter bounded by the trait, or implement it in the extend block.");

    public void ReportConstantOverflow(TextSpan span) =>
        Error("PIP0292", span, "This constant expression overflows. Constant arithmetic is checked, as in C#; use a long or a variable if wrapping around is intended.");

    public void ReportConstantDivisionByZero(TextSpan span) =>
        Error("PIP0293", span, "Division by constant zero.");

    public void ReportNotDisposable(TextSpan span, TypeSymbol type, string requiredInterface) =>
        Error("PIP0294", span, $"'{type}' can't be used with 'using': it doesn't implement System.{requiredInterface}.");

    public void ReportNotAsyncIterable(TextSpan span, TypeSymbol type) =>
        Error("PIP0302", span, $"'await for' needs an IAsyncEnumerable<T>, but '{type}' isn't one.");

    public void ReportCannotCast(TextSpan span, TypeSymbol from, TypeSymbol to) =>
        Error("PIP0295", span, $"Cannot cast '{from}' to '{to}'.");

    public void ReportOrPatternBinding(TextSpan span, string name, string problem) =>
        Error("PIP0296", span, $"'{name}' {problem}: each alternative of a '|' pattern must bind the same variables, with the same types.");

    public void ReportInvalidAsyncReturnType(TextSpan span, string what, TypeSymbol type) =>
        Error("PIP0297", span, $"An async {what} must return Task, Task<T>, ValueTask or ValueTask<T>, not '{type}'.");

    public void ReportAsyncWithoutAwait(TextSpan span, string what) =>
        Warning("PIP0298", span, $"This async {what} has no 'await', so it runs synchronously.");

    public void ReportAwaitOutsideAsync(TextSpan span) =>
        Error("PIP0299", span, "'await' can only be used in an async function or lambda; mark it 'async' and return a Task.");

    public void ReportNotAwaitable(TextSpan span, TypeSymbol type) =>
        Error("PIP0300", span, $"A '{type}' can't be awaited; await a Task, a ValueTask, or anything with a GetAwaiter() method.");

    public void ReportAsyncWithoutBody(TextSpan span) =>
        Error("PIP0301", span, "A required trait method can't be 'async'; mark the implementations async instead.");
    public void ReportInvalidIteratorType(TextSpan span, TypeSymbol type, bool isAsync) =>
        Error("PIP0303", span, isAsync
            ? $"An async function that yields must return IAsyncEnumerable<T> or IAsyncEnumerator<T>, not '{type}'."
            : $"A function that yields must return IEnumerable<T> or IEnumerator<T> (or IAsyncEnumerable<T> if async), not '{type}'.");

    public void ReportYieldOutsideIterator(TextSpan span) =>
        Error("PIP0304", span, "'yield' can only be used in a function's body (a lambda can't be an iterator), as in C#.");

    public void ReportReturnInIterator(TextSpan span) =>
        Error("PIP0305", span, "An iterator can't 'return'; use 'yield return value;' to produce a value or 'yield break;' to stop.");

    public void ReportYieldInTry(TextSpan span, string where) =>
        Error("PIP0306", span, $"'yield' can't be used {where}, as in C#.");
    public void ReportSpreadNotRecord(TextSpan span, TypeSymbol type) =>
        Error("PIP0307", span, $"Only a record can be spread into a copy ({{ ...value, field: … }}); '{type}' isn't one.");

    public void ReportFieldUpdatedTwice(TextSpan span, string field) =>
        Error("PIP0308", span, $"Field '{field}' is set more than once.");

    public void ReportSpreadNotEnumerable(TextSpan span, TypeSymbol type) =>
        Error("PIP0309", span, $"Only a sequence (an IEnumerable<T>) can be spread into a collection ([...items]); '{type}' isn't one.");

    public void ReportSpreadElementConversion(TextSpan span, TypeSymbol from, TypeSymbol to) =>
        Error("PIP0310", span, $"Can't spread '{from}' items into a collection of '{to}'.");

    public void ReportEmptyRangePattern(TextSpan span) =>
        Error("PIP0239", span, "This range pattern is empty: its lower bound is not below its upper bound.");
}
