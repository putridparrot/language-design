using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using Piper.Binding;
using Piper.Text;

namespace Piper.Emit;

/// <summary>
/// Translates a lowered <see cref="BoundProgram"/> into C# source text. Records become top-level
/// <c>sealed record</c>s, functions become static methods of a module class, and a small entry
/// point class calls <c>main</c>. Locals are renamed where needed so every variable in a method
/// has a unique C# name (Piper allows shadowing; C# does not).
/// </summary>
public sealed class CSharpEmitter
{
    public const string EntryPointClassName = "PiperEntryPoint";

    // Mirrors C#'s operator precedence; higher binds tighter.
    private const int AssignmentPrecedence = 1;
    private const int ConditionalPrecedence = 2;
    private const int LogicalOrPrecedence = 3;
    private const int LogicalAndPrecedence = 4;
    private const int EqualityPrecedence = 5;
    private const int RelationalPrecedence = 6;
    private const int AdditivePrecedence = 7;
    private const int MultiplicativePrecedence = 8;
    private const int UnaryPrecedence = 9;
    private const int PrimaryPrecedence = 10;

    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        // Contextual keywords that change meaning in positions we emit identifiers in.
        "var", "dynamic", "nint", "nuint", "record", "async", "await", "nameof", "value", "field", "scoped",
        "file", "required", "partial", "with", "when", "and", "or", "not", "global", "managed", "unmanaged",
    ];


    public const string InteropClassName = "PiperInterop";

    private readonly BoundProgram _program;
    private readonly Dictionary<MethodSymbol, BoundFunction> _methodBodies;
    private readonly HashSet<string> _topLevelNames;
    private readonly Dictionary<SourceText, string> _moduleClassNames = [];
    private readonly Dictionary<SourceText, string> _extensionClassNames = [];
    private readonly SourceText _runtimeFile;
    private CodeWriter _writer = new();
    private NameAllocator _names = new([]);

    /// <summary>How <c>this</c> is spelled in the method being emitted: <c>this</c> in members, the receiver parameter in extension blocks.</summary>
    private string _thisName = "this";

    /// <summary>The namespace whose block is being written; types elsewhere are written fully qualified.</summary>
    private NamespaceSymbol? _namespace;

    /// <summary>Set while writing a file's module class: that file's functions are called unqualified.</summary>
    private SourceText? _moduleFile;

    /// <summary>Option/Result and the interop helpers are only emitted if the program uses them.</summary>
    private readonly HashSet<EnumSymbol> _usedBuiltins = [];
    private bool _usesInterop;
    private bool _usesOutConversion;

    /// <summary>Whether to write `#line` directives mapping the C# back to the Piper source.</summary>
    private readonly bool _lineDirectives;

    /// <summary>The Piper file whose code is being written (null in runtime support), and the last line mapped to.</summary>
    private SourceText? _lineFile;
    private int _lastLine = -1;

    private CSharpEmitter(BoundProgram program, bool lineDirectives)
    {
        _program = program;
        _lineDirectives = lineDirectives;
        _methodBodies = program.Methods.ToDictionary(m => (MethodSymbol)m.Symbol);
        _topLevelNames =
        [
            .. program.Types.Select(t => t.Name), .. program.BuiltinTypes.Select(t => t.Name),
            .. program.Functions.Select(f => f.Symbol.Name), EntryPointClassName, InteropClassName,
            // The entry point's own Main method would hide a module class of that name.
            "Main",
        ];
        foreach (var file in program.Files)
        {
            var moduleName = UniqueTopLevelName(ModuleName(file.FileName), "Module");
            _moduleClassNames[file] = moduleName;
            _extensionClassNames[file] = UniqueTopLevelName(moduleName + "Extensions", "_");
        }
        _runtimeFile = program.Main?.DeclaringFile ?? program.Files[0];
    }

    private string UniqueTopLevelName(string name, string suffix)
    {
        while (_topLevelNames.Contains(name))
            name += suffix;
        _topLevelNames.Add(name);
        return name;
    }

    /// <summary>`hello_world.piper` → `Hello_world`; falls back to `Program`.</summary>
    public static string ModuleName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var builder = new StringBuilder();
        foreach (char c in stem)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');

        if (builder.Length == 0 || builder.ToString().All(c => c == '_'))
            return "Program";
        if (char.IsAsciiDigit(builder[0]))
            builder.Insert(0, '_');
        builder[0] = char.ToUpperInvariant(builder[0]);
        return builder.ToString();
    }

    /// <summary>One C# file per Piper file (named after it); the file with <c>main</c> also gets the entry point and runtime support.</summary>
    /// <param name="lineDirectives">
    /// Write `#line` directives so compiler errors, stack traces and debuggers point at the `.piper` source.
    /// </param>
    public static ImmutableArray<GeneratedFile> Emit(BoundProgram program, bool lineDirectives = false)
    {
        var emitter = new CSharpEmitter(program, lineDirectives);
        var texts = program.Files.Select((file, i) => (File: file, Text: emitter.EmitFile(file, program.FileNamespaces[i]))).ToList();

        // Runtime support goes last, once every file has been written and we know what was used.
        var runtimeIndex = texts.FindIndex(t => t.File == emitter._runtimeFile);
        texts[runtimeIndex] = (emitter._runtimeFile, texts[runtimeIndex].Text + emitter.EmitRuntime());

        return [.. texts.Select(t => new GeneratedFile(Path.ChangeExtension(Path.GetFileName(t.File.FileName), ".cs"), t.Text))];
    }

    private string EmitFile(SourceText file, NamespaceSymbol @namespace)
    {
        _writer = new CodeWriter();
        _lineFile = file;
        _writer.WriteLine("// <auto-generated>");
        _writer.WriteLine("//     Generated by the Piper compiler. Do not edit by hand.");
        _writer.WriteLine("// </auto-generated>");
        _writer.WriteLine("#nullable enable");
        _writer.WriteLine();

        // Methods and trait implementations this file adds to types declared in other files go in a
        // `partial` part, inside the namespace of the type they extend.
        var contributions = ForeignContributions(file).ToList();
        InNamespace(@namespace, () =>
        {
            var sections = new List<Action>();
            foreach (var type in _program.Types.Where(t => t.DeclaringFile == file))
            {
                sections.Add(type switch
                {
                    RecordSymbol record => () => EmitRecord(record, file),
                    EnumSymbol @enum => () => EmitEnum(@enum, file),
                    TraitSymbol trait => () => EmitTrait(trait),
                    _ => throw new InvalidOperationException(),
                });
            }
            foreach (var type in contributions.Where(t => NamespaceOf(t) == @namespace))
                sections.Add(() => EmitPartialPart(type, file));

            var functions = _program.Functions.Where(f => f.Symbol.DeclaringFile == file).ToList();
            if (functions.Count > 0 || file == _runtimeFile)
                sections.Add(() => EmitModuleClass(file, functions));
            if (HasExtensions(file))
                sections.Add(() => EmitExtensions(file));

            for (int i = 0; i < sections.Count; i++)
            {
                if (i > 0)
                    _writer.WriteLine();
                sections[i]();
            }
        });

        foreach (var group in contributions.Where(t => NamespaceOf(t) != @namespace).GroupBy(NamespaceOf))
        {
            _writer.WriteLine();
            InNamespace(group.Key, () =>
            {
                var types = group.ToList();
                for (int i = 0; i < types.Count; i++)
                {
                    if (i > 0)
                        _writer.WriteLine();
                    EmitPartialPart(types[i], file);
                }
            });
        }
        return _writer.ToString();
    }

    private NamespaceSymbol NamespaceOf(Symbol symbol) => symbol.Namespace ?? RootOf(_program.FileNamespaces[0]);

    private void EmitModuleClass(SourceText file, List<BoundFunction> functions)
    {
        _writer.WriteLine($"public static class {Escape(_moduleClassNames[file])}");
        _writer.OpenBrace();
        _moduleFile = file;
        for (int i = 0; i < functions.Count; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            EmitFunction(functions[i], functions[i].Symbol.IsPrivate ? "internal static " : "public static ");
        }
        _moduleFile = null;
        _writer.CloseBrace();
    }

    /// <summary>Writes <paramref name="body"/> inside a <c>namespace</c> block (none for the global namespace).</summary>
    private void InNamespace(NamespaceSymbol @namespace, Action body)
    {
        var outer = _namespace;
        _namespace = @namespace;
        if (!@namespace.IsGlobal)
        {
            _writer.WriteLine($"namespace {@namespace.FullName}");
            _writer.OpenBrace();
        }
        body();
        if (!@namespace.IsGlobal)
            _writer.CloseBrace();
        _namespace = outer;
    }

    /// <summary>The file a type's main declaration is written to: its own, or (for Option/Result) the runtime file.</summary>
    private SourceText HomeFile(NamedTypeDefinition type) => type.DeclaringFile ?? _runtimeFile;

    private IEnumerable<MethodSymbol> MethodsFrom(NamedTypeDefinition type, SourceText file) =>
        type.Methods.Where(m => (m.DeclaringFile ?? _runtimeFile) == file);

    private IEnumerable<TraitSymbol> TraitsFrom(NamedTypeDefinition type, SourceText file) =>
        type.Conformances.Where(c => (c.File ?? _runtimeFile) == file).Select(c => c.Trait);

    /// <summary>Other files add to this type with extend blocks, so every C# part must be <c>partial</c>.</summary>
    private bool IsPartial(NamedTypeDefinition type) =>
        type.Methods.Any(m => (m.DeclaringFile ?? _runtimeFile) != HomeFile(type)) ||
        type.Conformances.Any(c => (c.File ?? _runtimeFile) != HomeFile(type));

    private IEnumerable<NamedTypeDefinition> ForeignContributions(SourceText file) =>
        _program.Types.OfType<NamedTypeDefinition>().Concat(_program.BuiltinTypes)
            .Where(t => HomeFile(t) != file && (MethodsFrom(t, file).Any() || TraitsFrom(t, file).Any()));

    private static string Visibility(Symbol symbol) => symbol.IsPrivate ? "file" : "public";

    /// <summary>
    /// <c>public sealed record Pair&lt;A, B&gt;(A first, B second) : Trait where A : Bound</c>, with a body
    /// holding any extend methods (which is what lets the record implement trait interfaces).
    /// </summary>
    private void EmitRecord(RecordSymbol record, SourceText file)
    {
        var partial = IsPartial(record) ? "partial " : "";
        var header = $"{Visibility(record)} sealed {partial}record {Escape(record.Name)}{TypeParameterList(record.TypeParameters)}" +
                     $"({Fields(record.Fields)}){BaseList(TraitsFrom(record, file))}{Constraints(record.TypeParameters)}";
        var methods = MethodsFrom(record, file).ToList();
        if (methods.Count == 0)
        {
            _writer.WriteLine(header + ";");
            return;
        }

        _writer.WriteLine(header);
        _writer.OpenBrace();
        EmitMembers(methods);
        _writer.CloseBrace();
    }

    /// <summary>
    /// An enum is a closed record hierarchy: the private constructor means only the nested variant
    /// records can derive from it.
    /// </summary>
    private void EmitEnum(EnumSymbol @enum, SourceText file)
    {
        var name = Escape(@enum.Name);
        var selfType = TypeName(@enum.SelfType);
        // Inside the enum a variant name hides any top-level type of the same name, so qualify those.
        var variantNames = @enum.Variants.Select(v => v.Name).ToHashSet();
        var partial = IsPartial(@enum) ? "partial " : "";

        _writer.WriteLine($"{Visibility(@enum)} abstract {partial}record {name}{TypeParameterList(@enum.TypeParameters)}" +
                          $"{BaseList(TraitsFrom(@enum, file))}{Constraints(@enum.TypeParameters)}");
        _writer.OpenBrace();
        _writer.WriteLine($"private {name}()");
        _writer.OpenBrace();
        _writer.CloseBrace();
        foreach (var variant in @enum.Variants)
        {
            _writer.WriteLine();
            var header = $"public sealed record {Escape(variant.Name)}({Fields(variant.Fields, variantNames)}) : {selfType}";
            // A fieldless variant prints like a C# enum member (`Food`), not as an empty record (`Food { }`).
            _writer.WriteLine(variant.Fields.IsEmpty
                ? $"{header} {{ public override string ToString() => {Literal(variant.Name)}; }}"
                : $"{header};");
        }
        var methods = MethodsFrom(@enum, file).ToList();
        if (methods.Count > 0)
        {
            _writer.WriteLine();
            EmitMembers(methods);
        }
        _writer.CloseBrace();
    }

    /// <summary>A part of a type declared in another file, holding what this file's extend blocks add to it.</summary>
    private void EmitPartialPart(NamedTypeDefinition type, SourceText file)
    {
        var keyword = type is EnumSymbol { BuiltinKind: not BuiltinEnumKind.None } ? "readonly partial record struct" : "partial record";
        _writer.WriteLine($"{keyword} {Escape(type.Name)}{TypeParameterList(type.TypeParameters)}{BaseList(TraitsFrom(type, file))}");
        _writer.OpenBrace();
        EmitMembers(MethodsFrom(type, file).ToList());
        _writer.CloseBrace();
    }

    /// <summary>A trait is an interface; methods with a body become default interface methods.</summary>
    private void EmitTrait(TraitSymbol trait)
    {
        _writer.WriteLine($"{Visibility(trait)} interface {Escape(trait.Name)}");
        _writer.OpenBrace();
        for (int i = 0; i < trait.Methods.Length; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            var method = trait.Methods[i];
            _thisName = "this";
            if (_methodBodies.TryGetValue(method, out var body))
            {
                EmitFunction(body, "");
            }
            else
            {
                _names = NewNameAllocator(method);
                _writer.WriteLine($"{Signature(method, "")};");
            }
        }
        _writer.CloseBrace();
    }

    private void EmitMembers(IReadOnlyList<MethodSymbol> methods)
    {
        _thisName = "this";
        for (int i = 0; i < methods.Count; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            var access = methods[i].IsPrivate ? "internal " : "public ";
            EmitFunction(_methodBodies[methods[i]], access + (methods[i].IsStatic ? "static " : ""));
        }
    }

    /// <summary>
    /// Methods on builtin and .NET types become C# 14 extension members:
    /// <c>extension(int self) { public int squared() =&gt; self * self; }</c>.
    /// </summary>
    private IEnumerable<MethodSymbol> ExtensionMethods(SourceText file) => _program.Methods
        .Select(m => (MethodSymbol)m.Symbol)
        .Where(m => m.DeclaringFile == file && m.ContainingType is not (NamedTypeDefinition or TraitSymbol));

    private bool HasExtensions(SourceText file) => ExtensionMethods(file).Any();

    private void EmitExtensions(SourceText file)
    {
        var groups = ExtensionMethods(file).GroupBy(m => (m.ContainingType, m.IsStatic)).ToList();

        _writer.WriteLine($"public static class {Escape(_extensionClassNames[file])}");
        _writer.OpenBrace();
        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            var (type, isStatic) = groups[i].Key;
            _writer.WriteLine(isStatic ? $"extension({TypeName(type)})" : $"extension({TypeName(type)} self)");
            _writer.OpenBrace();
            var methods = groups[i].ToList();
            for (int j = 0; j < methods.Count; j++)
            {
                if (j > 0)
                    _writer.WriteLine();
                _thisName = "self";
                var access = methods[j].IsPrivate ? "internal " : "public ";
                EmitFunction(_methodBodies[methods[j]], access + (isStatic ? "static " : ""));
            }
            _writer.CloseBrace();
        }
        _writer.CloseBrace();
        _thisName = "this";
    }

    /// <summary>The entry point, the Option/Result structs and the null-boundary helpers, in the global namespace.</summary>
    private string EmitRuntime()
    {
        _writer = new CodeWriter();
        _lineFile = null;
        _namespace = _program.FileNamespaces[0].IsGlobal ? _program.FileNamespaces[0] : RootOf(_program.FileNamespaces[0]);

        if (_program.Main is { } main)
        {
            _writer.WriteLine();
            _writer.WriteLine($"internal static class {EntryPointClassName}");
            _writer.OpenBrace();
            _writer.WriteLine("public static void Main()");
            _writer.OpenBrace();
            // Piper output must not depend on the machine's locale (e.g. "1,5" vs "1.5").
            _writer.WriteLine("global::System.Globalization.CultureInfo.CurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;");
            _writer.WriteLine($"{FunctionContainer(main)}.{Escape(main.Name)}();");
            _writer.CloseBrace();
            _writer.CloseBrace();
        }

        if (_usesInterop)
            EmitInterop();

        // Emitted last, once we know whether anything used them.
        foreach (var builtin in _program.BuiltinTypes)
        {
            if (_usedBuiltins.Contains(builtin) || builtin.Methods.Count > 0)
                EmitBuiltinType(builtin);
        }
        return _writer.ToString();
    }

    private static NamespaceSymbol RootOf(NamespaceSymbol @namespace)
    {
        while (@namespace.Parent is not null)
            @namespace = @namespace.Parent;
        return @namespace;
    }

    /// <summary>Where .NET's null meets Piper's Option.</summary>
    private void EmitInterop()
    {
        _usedBuiltins.Add(_program.BuiltinTypes[0]);
        _writer.WriteLine();
        _writer.WriteLine($"internal static class {InteropClassName}");
        _writer.OpenBrace();
        _writer.WriteLine("public static Option<T> FromNullableReference<T>(T? value) where T : class => value is null ? Option<T>.None : Option<T>.Some(value);");
        _writer.WriteLine();
        _writer.WriteLine("public static Option<T> FromNullableValue<T>(T? value) where T : struct => value.HasValue ? Option<T>.Some(value.GetValueOrDefault()) : Option<T>.None;");
        _writer.WriteLine();
        _writer.WriteLine("public static T? ToNullableReference<T>(Option<T> value) where T : class => value.IsSome ? value.Value : null;");
        _writer.WriteLine();
        _writer.WriteLine("public static T? ToNullableValue<T>(Option<T> value) where T : struct => value.IsSome ? value.Value : null;");
        if (_usesOutConversion)
            EmitOutConversions();
        _writer.CloseBrace();
    }

    private void EmitOutConversions()
    {
        _writer.WriteLine();
        // `out let v` where .NET may write null: the call's own result passes through, and v gets the converted value.
        _writer.WriteLine("public static TResult AfterOutReference<TResult, T>(TResult result, T? value, out Option<T> converted) where T : class");
        _writer.OpenBrace();
        _writer.WriteLine("converted = FromNullableReference(value);");
        _writer.WriteLine("return result;");
        _writer.CloseBrace();
        _writer.WriteLine();
        _writer.WriteLine("public static TResult AfterOutValue<TResult, T>(TResult result, T? value, out Option<T> converted) where T : struct");
        _writer.OpenBrace();
        _writer.WriteLine("converted = FromNullableValue(value);");
        _writer.WriteLine("return result;");
        _writer.CloseBrace();
    }

    /// <summary>
    /// Option and Result are small readonly structs rather than record hierarchies: no allocation, they
    /// nest (<c>Int??</c>), and <c>default</c> is None. Patterns use their properties, e.g.
    /// <c>{ IsSome: true, Value: var v }</c>.
    /// </summary>
    private void EmitBuiltinType(EnumSymbol builtin)
    {
        bool isOption = builtin.BuiltinKind == BuiltinEnumKind.Option;
        var partial = IsPartial(builtin) ? "partial " : "";
        _writer.WriteLine();
        _writer.WriteLine("#nullable disable");
        _writer.WriteLine($"public readonly {partial}record struct {(isOption ? "Option<T>" : "Result<T, E>")}{BaseList(TraitsFrom(builtin, _runtimeFile))}");
        _writer.OpenBrace();
        if (isOption)
        {
            _writer.WriteLine("private Option(T value)");
            _writer.OpenBrace();
            _writer.WriteLine("IsSome = true;");
            _writer.WriteLine("Value = value;");
            _writer.CloseBrace();
            _writer.WriteLine();
            _writer.WriteLine("public bool IsSome { get; }");
            _writer.WriteLine();
            _writer.WriteLine("public T Value { get; }");
            _writer.WriteLine();
            _writer.WriteLine("public static Option<T> Some(T value) => new(value);");
            _writer.WriteLine();
            _writer.WriteLine("public static Option<T> None => default;");
            _writer.WriteLine();
            _writer.WriteLine("public override string ToString() => IsSome ? $\"Some({Value})\" : \"None\";");
        }
        else
        {
            _writer.WriteLine("private Result(bool isOk, T value, E error)");
            _writer.OpenBrace();
            _writer.WriteLine("IsOk = isOk;");
            _writer.WriteLine("Value = value;");
            _writer.WriteLine("Error = error;");
            _writer.CloseBrace();
            _writer.WriteLine();
            _writer.WriteLine("public bool IsOk { get; }");
            _writer.WriteLine();
            _writer.WriteLine("public T Value { get; }");
            _writer.WriteLine();
            _writer.WriteLine("public E Error { get; }");
            _writer.WriteLine();
            _writer.WriteLine("public static Result<T, E> Ok(T value) => new(true, value, default);");
            _writer.WriteLine();
            _writer.WriteLine("public static Result<T, E> Err(E error) => new(false, default, error);");
            _writer.WriteLine();
            _writer.WriteLine("public override string ToString() => IsOk ? $\"Ok({Value})\" : $\"Err({Error})\";");
        }

        var methods = MethodsFrom(builtin, _runtimeFile).ToList();
        if (methods.Count > 0)
        {
            _writer.WriteLine();
            EmitMembers(methods);
        }
        _writer.CloseBrace();
        _writer.WriteLine("#nullable restore");
    }

    private string Fields(IEnumerable<FieldSymbol> fields, ISet<string>? shadowedNames = null) =>
        string.Join(", ", fields.Select(f => $"{TypeName(f.Type, shadowedNames)} {Escape(f.Name)}"));

    private static string TypeParameterList(IEnumerable<TypeParameterSymbol> parameters) =>
        parameters.Any() ? $"<{string.Join(", ", parameters.Select(p => Escape(p.Name)))}>" : "";

    private string BaseList(IEnumerable<TraitSymbol> traits) =>
        traits.Any() ? $" : {string.Join(", ", traits.Select(t => TypeName(t)))}" : "";

    private string Constraints(IEnumerable<TypeParameterSymbol> parameters) =>
        string.Concat(parameters
            .Where(p => p.Constraints.Length > 0)
            .Select(p => $" where {Escape(p.Name)} : {string.Join(", ", p.Constraints.Select(c => TypeName(c)))}"));

    /// <summary>How to reach a top-level function from the current position: its module class, qualified as needed.</summary>
    private string FunctionContainer(FunctionSymbol function)
    {
        var className = Escape(_moduleClassNames[function.DeclaringFile!]);
        return function.Namespace == _namespace ? className : $"global::{Qualified(function.Namespace!, className)}";
    }

    private static string Qualified(NamespaceSymbol @namespace, string name) =>
        @namespace.IsGlobal ? name : $"{@namespace.FullName}.{name}";

    private NameAllocator NewNameAllocator(FunctionSymbol function)
    {
        IEnumerable<string> typeParameters = function.TypeParameters.Select(p => p.Name);
        if (function is MethodSymbol { ContainingType: NamedTypeDefinition definition })
            typeParameters = typeParameters.Concat(definition.TypeParameters.Select(p => p.Name));
        // In an extension block the receiver parameter is called `self` (C# can't name it `this`), so keep that name free.
        return new NameAllocator([.. _topLevelNames, "_", "self", .. typeParameters]);
    }

    /// <summary>e.g. <c>public static T first&lt;T&gt;(Pair&lt;T, T&gt; pair) where T : Show</c>; declares the parameter names.</summary>
    private string Signature(FunctionSymbol symbol, string modifiers)
    {
        var parameters = string.Join(", ", symbol.Parameters.Select(p => $"{TypeName(p.Type)} {Escape(_names.Declare(p))}"));
        return $"{modifiers}{TypeName(symbol.ReturnType)} {Escape(symbol.Name)}{TypeParameterList(symbol.TypeParameters)}" +
               $"({parameters}){Constraints(symbol.TypeParameters)}";
    }

    private void EmitFunction(BoundFunction function, string modifiers)
    {
        var symbol = function.Symbol;
        _names = NewNameAllocator(symbol);
        if (symbol.Declaration is { } declaration)
            LineDirective(declaration.Span);
        try
        {
            EmitFunctionBody(function, modifiers);
        }
        finally
        {
            // Code after the function (other members, the next type) isn't from any Piper line.
            if (_lineDirectives && _lastLine >= 0)
            {
                _writer.WriteLine("#line default");
                _lastLine = -1;
            }
        }
    }

    private void EmitFunctionBody(BoundFunction function, string modifiers)
    {
        var symbol = function.Symbol;

        var signature = Signature(symbol, modifiers);
        var body = (BoundBlockStatement)function.Body;

        if (function.IsExpressionBodied && ExpressionBody(body) is { } expression)
        {
            _writer.WriteLine($"{signature} => {Expr(expression, AssignmentPrecedence)};");
            return;
        }

        _writer.WriteLine(signature);
        EmitBlock(body);
    }

    /// <summary>The expression a lowered body consists of, if it can be written as <c>=&gt; expression</c>.</summary>
    private static BoundExpression? ExpressionBody(BoundBlockStatement body) => body.Statements switch
    {
        [BoundReturnStatement { Value: { } value }] => value,
        [BoundExpressionStatement { Expression: var expression }] when IsStatementExpression(expression) && !IsVoidCallWithOutConversions(expression) => expression,
        _ => null,
    };

    /// <summary>C# only allows calls and assignments as expression statements.</summary>
    private static bool IsStatementExpression(BoundExpression expression) =>
        expression is BoundCallExpression or BoundMethodCallExpression or BoundClrInvocationExpression or BoundAssignmentExpression
            or BoundDelegateInvocationExpression or BoundClrAssignmentExpression or BoundDeconstructionExpression;

    // Statements

    private void EmitBlock(BoundStatement statement)
    {
        _writer.OpenBrace();
        if (statement is BoundBlockStatement block)
        {
            foreach (var inner in block.Statements)
                EmitStatement(inner);
        }
        else
        {
            EmitStatement(statement);
        }
        _writer.CloseBrace();
    }

    /// <summary>`#line 12 "file.piper"` before code from a different Piper line than the code before it.</summary>
    private void LineDirective(TextSpan? span)
    {
        if (!_lineDirectives || _lineFile is null || span is not { } location)
            return;
        int line = _lineFile.GetLineColumn(location.Start).Line;
        if (line == _lastLine)
            return;
        _writer.WriteLine($"#line {line} \"{_lineFile.FileName}\"");
        _lastLine = line;
    }

    private void EmitStatement(BoundStatement statement)
    {
        // Blocks only group statements; their braces aren't worth a directive of their own.
        if (statement is not BoundBlockStatement)
            LineDirective(statement.SourceSpan);
        switch (statement)
        {
            case BoundBlockStatement block:
                EmitBlock(block);
                break;

            case BoundVariableDeclaration declaration:
            {
                var type = TypeName(declaration.Variable.Type);
                var name = Escape(_names.Declare(declaration.Variable));
                var @using = declaration.IsUsing ? "using " : "";
                _writer.WriteLine(declaration.Initializer is null
                    ? $"{type} {name};"
                    : $"{@using}{type} {name} = {Expr(declaration.Initializer, AssignmentPrecedence)};");
                break;
            }

            case BoundUsingStatement usingStatement:
            {
                var resource = Expr(usingStatement.Resource, AssignmentPrecedence);
                _writer.WriteLine(usingStatement.Variable is { } variable
                    ? $"using ({TypeName(variable.Type)} {Escape(_names.Declare(variable))} = {resource})"
                    : $"using ({resource})");
                EmitBlock(usingStatement.Body);
                break;
            }

            case BoundExpressionStatement { Expression: BoundClrInvocationExpression invocation } when IsVoidCallWithOutConversions(invocation):
            {
                var (call, conversions) = ClrCallWithOutTemps(invocation);
                _writer.WriteLine($"{call};");
                foreach (var (temp, output) in conversions)
                {
                    var kind = output.Boundary == ClrBoundary.NullableReference ? "Reference" : "Value";
                    _writer.WriteLine($"{Escape(_names.Get(output.Variable!))} = global::{InteropClassName}.FromNullable{kind}({temp});");
                }
                break;
            }

            case BoundExpressionStatement expressionStatement when IsStatementExpression(expressionStatement.Expression):
                _writer.WriteLine($"{Expr(expressionStatement.Expression, AssignmentPrecedence)};");
                break;

            case BoundExpressionStatement expressionStatement:
                // C# only allows calls and assignments as statements, so discard any other value.
                _writer.WriteLine($"_ = {Expr(expressionStatement.Expression, AssignmentPrecedence)};");
                break;

            case BoundReturnStatement { Value: null }:
                _writer.WriteLine("return;");
                break;

            case BoundReturnStatement { Value: { } value }:
                _writer.WriteLine($"return {Expr(value, AssignmentPrecedence)};");
                break;

            case BoundIfStatement @if:
                EmitIf(@if, isElseIf: false);
                break;

            case BoundWhileStatement loop:
                _writer.WriteLine($"while ({Expr(loop.Condition, AssignmentPrecedence)})");
                EmitBlock(loop.Body);
                break;

            case BoundForStatement loop:
            {
                var type = TypeName(loop.Variable.Type);
                var lower = Expr(loop.Lower, AssignmentPrecedence);
                var upper = Expr(loop.Upper, RelationalPrecedence + 1);
                var name = Escape(_names.Declare(loop.Variable));
                var comparison = loop.IsInclusive ? "<=" : "<";
                _writer.WriteLine($"for ({type} {name} = {lower}; {name} {comparison} {upper}; {name}++)");
                EmitBlock(loop.Body);
                break;
            }

            case BoundForEachStatement loop:
            {
                var collection = Expr(loop.Collection, AssignmentPrecedence);
                var variable = loop.Deconstruction is { } deconstruction
                    ? $"var {Designation(deconstruction, declares: true)}"
                    : $"{TypeName(loop.Variable.Type)} {Escape(_names.Declare(loop.Variable))}";
                _writer.WriteLine($"foreach ({variable} in {collection})");
                EmitBlock(loop.Body);
                break;
            }

            case BoundTryCatchStatement attempt:
                EmitTryCatch(attempt);
                break;

            case BoundThrowStatement thrown:
                _writer.WriteLine($"throw {Expr(thrown.Exception, AssignmentPrecedence)};");
                break;

            case BoundUnreachableStatement:
                _writer.WriteLine("throw new global::System.Diagnostics.UnreachableException();");
                break;

            case BoundBreakStatement:
                _writer.WriteLine("break;");
                break;

            case BoundContinueStatement:
                _writer.WriteLine("continue;");
                break;

            default:
                throw new InvalidOperationException($"Unexpected statement {statement.GetType().Name}");
        }
    }

    private void EmitTryCatch(BoundTryCatchStatement attempt)
    {
        _writer.WriteLine("try");
        EmitBlock(attempt.Body);
        foreach (var clause in attempt.Catches)
        {
            _writer.WriteLine((clause.ExceptionType, clause.Variable) switch
            {
                (_, { } variable) => $"catch ({TypeName(variable.Type)} {Escape(_names.Declare(variable))})",
                ({ } type, null) => $"catch ({TypeName(type)})",
                _ => "catch",
            });
            EmitBlock(clause.Body);
        }
        if (attempt.Finally is { } @finally)
        {
            _writer.WriteLine("finally");
            EmitBlock(@finally);
        }
    }

    private void EmitIf(BoundIfStatement @if, bool isElseIf)
    {
        _writer.WriteLine($"{(isElseIf ? "else " : "")}if ({Expr(@if.Condition, AssignmentPrecedence)})");
        EmitBlock(@if.Then);
        switch (@if.Else)
        {
            case BoundIfStatement elseIf:
                EmitIf(elseIf, isElseIf: true);
                break;
            case { } @else:
                _writer.WriteLine("else");
                EmitBlock(@else);
                break;
        }
    }

    // Expressions

    /// <summary>Emits <paramref name="expression"/>, parenthesized if it binds looser than <paramref name="minPrecedence"/>.</summary>
    private string Expr(BoundExpression expression, int minPrecedence)
    {
        // Conversions are all implicit widenings in C# too, so they need no syntax.
        if (expression is BoundConversionExpression conversion)
            return Expr(conversion.Expression, minPrecedence);

        var (text, precedence) = expression switch
        {
            BoundLiteralExpression literal => (Literal(literal.Value), PrimaryPrecedence),
            BoundVariableExpression { Variable.Kind: VariableKind.This } => (_thisName, PrimaryPrecedence),
            BoundVariableExpression variable => (Escape(_names.Get(variable.Variable)), PrimaryPrecedence),
            BoundUnaryExpression unary => (Unary(unary), UnaryPrecedence),
            BoundBinaryExpression binary => Binary(binary),
            BoundAssignmentExpression assignment => (Assignment(assignment), AssignmentPrecedence),
            BoundCallExpression call => (Call(call), PrimaryPrecedence),
            BoundMethodCallExpression call => (MethodCall(call), PrimaryPrecedence),
            BoundRecordCreationExpression creation =>
                ($"new {TypeName(creation.Type)}({Arguments(creation.Arguments)})", PrimaryPrecedence),
            // Option/Result: static factories on the struct, e.g. `Option<int>.Some(1)` and `Option<int>.None`.
            BoundVariantCreationExpression { Variant.Enum.BuiltinKind: not BuiltinEnumKind.None } creation =>
                ($"{TypeName(creation.Type)}.{creation.Variant.Name}{(creation.Arguments.IsEmpty ? "" : $"({Arguments(creation.Arguments)})")}", PrimaryPrecedence),
            BoundVariantCreationExpression creation =>
                ($"new {ConstructorName(creation.Variant, creation.Type)}({Arguments(creation.Arguments)})", PrimaryPrecedence),
            BoundOptionalMemberExpression member =>
                ($"{Expr(member.Operand, PrimaryPrecedence)}.{member.Member}", PrimaryPrecedence),
            BoundClrInvocationExpression { Member: ConstructorInfo } construction =>
                ($"new {TypeName(construction.ContainerType)}({Arguments(construction.Arguments)})", PrimaryPrecedence),
            BoundClrInvocationExpression invocation => (ClrInvocation(invocation), PrimaryPrecedence),
            BoundOutArgument output => (output.Variable is { } variable ? $"out {Escape(_names.Get(variable))}" : "out _", PrimaryPrecedence),
            BoundClrAssignmentExpression assignment => (ClrAssignment(assignment), AssignmentPrecedence),
            BoundIndexExpression index =>
                ($"{Expr(index.Target, PrimaryPrecedence)}[{Arguments(index.Arguments)}]", PrimaryPrecedence),
            BoundCollectionExpression collection => (Collection(collection), PrimaryPrecedence),
            // The operand is parenthesized unless primary: C# reads `(Point)-x` as a subtraction.
            BoundCastExpression cast => ($"({TypeName(cast.Type)}){Expr(cast.Operand, PrimaryPrecedence)}", UnaryPrecedence),
            BoundTupleExpression tuple => ($"({Arguments(tuple.Elements)})", PrimaryPrecedence),
            // Always by position: a name may not be part of the C# expression's static type.
            BoundTupleElementExpression element => ($"{Expr(element.Tuple, PrimaryPrecedence)}.Item{element.Index + 1}", PrimaryPrecedence),
            BoundDeconstructionExpression deconstruction => (
                $"{(deconstruction.Declares ? "var " : "")}{Designation(deconstruction.Designation, deconstruction.Declares)} = {Expr(deconstruction.Value, AssignmentPrecedence)}",
                AssignmentPrecedence),
            BoundArrayCreationExpression creation =>
                ($"new {ArrayTypeName((ClrTypeSymbol)creation.Type, Arguments(creation.Sizes))}", PrimaryPrecedence),
            BoundLambdaExpression lambda => (Lambda(lambda), AssignmentPrecedence),
            // A method group; it converts to the delegate type the context gives it.
            BoundFunctionReferenceExpression reference =>
                ($"{(reference.Function.DeclaringFile == _moduleFile ? "" : FunctionContainer(reference.Function) + ".")}{Escape(reference.Function.Name)}{TypeArgumentList(reference.TypeArguments)}", PrimaryPrecedence),
            BoundDelegateInvocationExpression invocation => (DelegateInvocation(invocation), PrimaryPrecedence),
            // Lowering leaves throw expressions only where C# allows them (a ?: branch, the right of ??), which can't be parenthesized.
            BoundThrowExpression thrown => ($"throw {Expr(thrown.Exception, LogicalOrPrecedence)}", PrimaryPrecedence),
            BoundClrMemberExpression member =>
                ($"{ClrReceiver(member.Receiver, member.ContainerType)}.{Escape(member.Member.Name)}", PrimaryPrecedence),
            BoundNullableBoundaryExpression boundary => (NullableBoundary(boundary), PrimaryPrecedence),
            BoundIsPatternExpression test =>
                ($"{Expr(test.Expression, PrimaryPrecedence)} is {Pattern(test.Pattern)}", RelationalPrecedence),
            BoundFieldAccessExpression access =>
                ($"{Expr(access.Target, PrimaryPrecedence)}.{Escape(access.Field.Name)}", PrimaryPrecedence),
            BoundInterpolatedStringExpression interpolated => (InterpolatedString(interpolated), PrimaryPrecedence),
            BoundConditionalExpression conditional => (
                $"{Expr(conditional.Condition, LogicalOrPrecedence)} ? {Branch(conditional.WhenTrue)} : {Branch(conditional.WhenFalse)}",
                ConditionalPrecedence),
            _ => throw new InvalidOperationException($"{expression.GetType().Name} should have been lowered or rejected before emit."),
        };

        return precedence < minPrecedence ? $"({text})" : text;
    }

    private string Unary(BoundUnaryExpression unary)
    {
        var op = unary.Operator == BoundUnaryOperatorKind.Negate ? "-" : "!";
        var operand = Expr(unary.Operand, UnaryPrecedence);
        // Avoid `--x` (decrement) for a double negation.
        if (op == "-" && operand.StartsWith('-'))
            operand = $"({operand})";
        return op + operand;
    }

    private (string, int) Binary(BoundBinaryExpression binary)
    {
        int precedence = binary.Operator switch
        {
            BoundBinaryOperatorKind.Multiply or BoundBinaryOperatorKind.Divide or BoundBinaryOperatorKind.Remainder => MultiplicativePrecedence,
            BoundBinaryOperatorKind.Add or BoundBinaryOperatorKind.Subtract => AdditivePrecedence,
            BoundBinaryOperatorKind.Less or BoundBinaryOperatorKind.LessOrEqual
                or BoundBinaryOperatorKind.Greater or BoundBinaryOperatorKind.GreaterOrEqual => RelationalPrecedence,
            BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals => EqualityPrecedence,
            BoundBinaryOperatorKind.LogicalAnd => LogicalAndPrecedence,
            BoundBinaryOperatorKind.LogicalOr => LogicalOrPrecedence,
            _ => throw new InvalidOperationException(),
        };

        // C# can't apply == to an unconstrained type parameter, and on an interface it would compare references.
        if (binary.Operator is BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals && NeedsEqualityComparer(binary.Left.Type))
        {
            var call = $"global::System.Collections.Generic.EqualityComparer<{TypeName(binary.Left.Type)}>.Default.Equals(" +
                       $"{Expr(binary.Left, AssignmentPrecedence)}, {Expr(binary.Right, AssignmentPrecedence)})";
            return binary.Operator == BoundBinaryOperatorKind.Equals ? (call, PrimaryPrecedence) : ("!" + call, UnaryPrecedence);
        }

        // Left-associative: the right operand must bind strictly tighter.
        var left = Expr(binary.Left, precedence);
        var right = Expr(binary.Right, precedence + 1);
        return ($"{left} {Operators.GetText(binary.Operator)} {right}", precedence);
    }

    private static bool NeedsEqualityComparer(TypeSymbol type) =>
        type is TypeParameterSymbol or TraitSymbol || (type is TupleTypeSymbol tuple && tuple.ElementTypes.Any(NeedsEqualityComparer));

    private string Assignment(BoundAssignmentExpression assignment)
    {
        var op = assignment.CompoundOperator is { } compound ? Operators.GetText(compound) + "=" : "=";
        return $"{Escape(_names.Get(assignment.Variable))} {op} {Expr(assignment.Value, AssignmentPrecedence)}";
    }

    private string ClrAssignment(BoundClrAssignmentExpression assignment)
    {
        var op = assignment.CompoundOperator is { } compound ? Operators.GetText(compound) + "=" : "=";
        return $"{Expr(assignment.Target, PrimaryPrecedence)} {op} {Expr(assignment.Value, AssignmentPrecedence)}";
    }

    /// <summary><c>new List&lt;int&gt; { 1, 2 }</c> or <c>new int[] { 1, 2 }</c>; works whatever the target type (unlike <c>[1, 2]</c>).</summary>
    private string Collection(BoundCollectionExpression collection)
    {
        var type = TypeName(collection.Type);
        if (collection.Elements.IsEmpty)
            return collection.Type is ClrTypeSymbol { Type.IsArray: true } ? $"new {type} {{ }}" : $"new {type}()";
        return $"new {type} {{ {Arguments(collection.Elements)} }}";
    }

    /// <summary>
    /// <c>(int x) =&gt; x + 1</c>. Parameter types are always written, which gives the lambda a natural type in C#
    /// too. A block body is written with a nested writer at the current indentation so it lines up with the code around it.
    /// </summary>
    private string Lambda(BoundLambdaExpression lambda)
    {
        var parameters = string.Join(", ", lambda.Parameters.Select(p => $"{TypeName(p.Type)} {Escape(_names.Declare(p))}"));
        var body = (BoundBlockStatement)lambda.Body;
        if (lambda.IsExpressionBodied && ExpressionBody(body) is { } expression)
            return $"({parameters}) => {Expr(expression, AssignmentPrecedence)}";

        var outer = _writer;
        _writer = new CodeWriter(outer.Indent);
        try
        {
            EmitBlock(body);
            return $"({parameters}) => {_writer.ToString().Trim()}";
        }
        finally
        {
            _writer = outer;
        }
    }

    /// <summary>
    /// <c>(a, (b, _))</c> of C#'s <c>var (a, (b, _)) = value</c> (declaring the names) or <c>(a, b) = value</c>
    /// (assigning existing variables). C# finds the tuple elements, a record's or a .NET type's Deconstruct.
    /// </summary>
    private string Designation(BoundParenthesizedDesignation designation, bool declares) =>
        $"({string.Join(", ", designation.Elements.Select(element => element switch
        {
            BoundVariableDesignation variable => Escape(declares ? _names.Declare(variable.Variable) : _names.Get(variable.Variable)),
            BoundParenthesizedDesignation nested => Designation(nested, declares),
            _ => "_",
        }))})";

    /// <summary><c>f(1)</c>; a lambda called directly is cast to its delegate type first, as C# can't invoke a lambda.</summary>
    private string DelegateInvocation(BoundDelegateInvocationExpression invocation)
    {
        var target = Unwrap(invocation.Target) is BoundLambdaExpression lambda
            ? $"(({TypeName(lambda.Type)})({Lambda(lambda)}))"
            : Expr(invocation.Target, PrimaryPrecedence);
        return $"{target}({Arguments(invocation.Arguments)})";
    }

    /// <summary>
    /// A ternary needs its branches to share a type, which two records implementing the same trait
    /// don't; casting a branch converted to a trait gives the expression that type.
    /// </summary>
    private string Branch(BoundExpression branch) =>
        branch is BoundConversionExpression { Type: TraitSymbol trait } conversion
            ? $"({TypeName(trait)}){Expr(conversion.Expression, UnaryPrecedence)}"
            : Expr(branch, ConditionalPrecedence);

    /// <summary>
    /// <c>receiver.Method&lt;T&gt;(args)</c>. An out argument whose variable is an Option (.NET may write null) goes to a
    /// temporary that a helper converts once the call returns:
    /// <c>PiperInterop.AfterOutReference(d.TryGetValue(k, out var t), t, out value)</c>.
    /// </summary>
    private string ClrInvocation(BoundClrInvocationExpression invocation)
    {
        var (call, conversions) = ClrCallWithOutTemps(invocation);
        _usesOutConversion |= conversions.Count > 0;
        foreach (var (temp, output) in conversions)
        {
            var kind = output.Boundary == ClrBoundary.NullableReference ? "Reference" : "Value";
            call = $"global::{InteropClassName}.AfterOut{kind}({call}, {temp}, out {Escape(_names.Get(output.Variable!))})";
        }
        return call;
    }

    /// <summary>The call with out-to-Option arguments written to temporaries, and those still to convert.</summary>
    private (string Call, List<(string Temp, BoundOutArgument Output)> Conversions) ClrCallWithOutTemps(BoundClrInvocationExpression invocation)
    {
        var conversions = new List<(string Temp, BoundOutArgument Output)>();
        var arguments = string.Join(", ", invocation.Arguments.Select(argument =>
        {
            if (argument is not BoundOutArgument { Boundary: not ClrBoundary.None, Variable: not null } output)
                return Arguments([argument]);
            _usesInterop = true;
            var temp = Escape(_names.Declare(new VariableSymbol("outValue", output.ClrType, isMutable: true, VariableKind.Temporary)));
            conversions.Add((temp, output));
            return $"out var {temp}";
        }));
        var call = $"{ClrReceiver(invocation.Receiver, invocation.ContainerType)}.{Escape(invocation.Member.Name)}" +
                   $"{TypeArgumentList(invocation.TypeArguments)}({arguments})";
        return (call, conversions);
    }

    /// <summary>A void .NET call with out-to-Option arguments, which can't pass through a helper: convert after it instead.</summary>
    private static bool IsVoidCallWithOutConversions(BoundExpression expression) =>
        expression is BoundClrInvocationExpression { Member: MethodInfo { ReturnType: var returns } } invocation && returns == typeof(void) &&
        invocation.Arguments.Any(a => a is BoundOutArgument { Boundary: not ClrBoundary.None, Variable: not null });

    /// <summary>The value an instance member is used on, or the type a static one belongs to.</summary>
    private string ClrReceiver(BoundExpression? receiver, TypeSymbol container) =>
        receiver is null ? TypeName(container) : Expr(receiver, PrimaryPrecedence);

    /// <summary>A call to one of the helpers that turn .NET's null into None and back.</summary>
    private string NullableBoundary(BoundNullableBoundaryExpression boundary)
    {
        _usesInterop = true;
        TypeName(boundary.Type);
        return $"global::{InteropClassName}.{boundary.Kind}({Expr(boundary.Operand, AssignmentPrecedence)})";
    }

    private string Call(BoundCallExpression call)
    {
        if (call.Function == BuiltinFunctions.Print)
            return $"global::System.Console.WriteLine({Expr(call.Arguments[0], ConditionalPrecedence)})";
        var qualifier = call.Function.DeclaringFile == _moduleFile ? "" : FunctionContainer(call.Function) + ".";
        return $"{qualifier}{Escape(call.Function.Name)}{TypeArgumentList(call.TypeArguments)}({Arguments(call.Arguments)})";
    }

    /// <summary>
    /// <c>receiver.method(args)</c> or <c>Type.method(args)</c>. C# only exposes a default interface method
    /// through the interface, so a default trait method called on a concrete type casts the receiver first.
    /// </summary>
    private string MethodCall(BoundMethodCallExpression call)
    {
        var method = $"{Escape(call.Method.Name)}{TypeArgumentList(call.TypeArguments)}({Arguments(call.Arguments)})";
        if (call.Receiver is null)
            return $"{TypeName(call.ReceiverType)}.{method}";

        if (call.Method.Trait is { } trait && call.ReceiverType is not (TraitSymbol or TypeParameterSymbol))
            return $"(({TypeName(trait)}){Expr(call.Receiver, UnaryPrecedence)}).{method}";

        return $"{Expr(call.Receiver, PrimaryPrecedence)}.{method}";
    }

    private string TypeArgumentList(ImmutableArray<TypeSymbol> arguments) =>
        arguments.IsEmpty ? "" : $"<{string.Join(", ", arguments.Select(a => TypeName(a)))}>";

    private string Arguments(IEnumerable<BoundExpression> arguments) =>
        string.Join(", ", arguments.Select(argument =>
        {
            // A lambda needs no parentheses in an argument list.
            var text = Expr(argument, Unwrap(argument) is BoundLambdaExpression ? AssignmentPrecedence : ConditionalPrecedence);
            // `f(a < b, c > d)` could be misread by C# as a generic instantiation; parenthesize to be safe.
            return Unwrap(argument) is BoundBinaryExpression { Operator: BoundBinaryOperatorKind.Less or BoundBinaryOperatorKind.Greater }
                ? $"({text})"
                : text;
        }));

    private string InterpolatedString(BoundInterpolatedStringExpression interpolated)
    {
        if (interpolated.Parts.All(p => p is BoundLiteralExpression { Value: string }))
            return Literal(string.Concat(interpolated.Parts.Select(p => (string)((BoundLiteralExpression)p).Value)));

        var builder = new StringBuilder("$\"");
        foreach (var part in interpolated.Parts)
        {
            if (part is BoundLiteralExpression { Value: string text })
                builder.Append(EscapeString(text, interpolated: true));
            else
            {
                // A top-level ':' would start a format specifier, so ternaries and `global::` names must be parenthesized.
                var hole = Expr(part, LogicalOrPrecedence);
                builder.Append('{').Append(hole.Contains("::") && !IsParenthesized(hole) ? $"({hole})" : hole).Append('}');
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>True if <paramref name="text"/> is wholly enclosed by one pair of parentheses (skipping string and char literals).</summary>
    private static bool IsParenthesized(string text)
    {
        if (!text.StartsWith('(') || !text.EndsWith(')'))
            return false;
        int depth = 0;
        char? quote = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote is not null)
            {
                if (c == '\\')
                    i++;
                else if (c == quote)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return i == text.Length - 1;
        }
        return false;
    }

    /// <summary>
    /// Piper patterns map onto C# patterns: variants and records become positional patterns (or type
    /// patterns when fieldless), ranges become relational patterns and '|' becomes 'or'.
    /// </summary>
    private string Pattern(BoundPattern pattern) => pattern switch
    {
        BoundWildcardPattern => "_",
        BoundBindingPattern binding => $"var {Escape(_names.Declare(binding.Variable))}",
        BoundLiteralPattern literal => Literal(literal.Value),
        BoundRangePattern range =>
            $">= {Literal(range.Lower)} and {(range.IsInclusive ? "<=" : "<")} {Literal(range.Upper)}",
        BoundConstructorPattern { Constructor: VariantSymbol { Enum.BuiltinKind: not BuiltinEnumKind.None } } builtin => BuiltinPattern(builtin),
        BoundConstructorPattern { Arguments.Length: 0 } constructor => ConstructorName(constructor.Constructor, constructor.Type),
        BoundConstructorPattern constructor =>
            $"{ConstructorName(constructor.Constructor, constructor.Type)}({string.Join(", ", constructor.Arguments.Select(Pattern))})",
        BoundTuplePattern tuple => $"({string.Join(", ", tuple.Elements.Select(Pattern))})",
        BoundOrPattern alternatives => string.Join(" or ", alternatives.Alternatives.Select(a =>
            a is BoundRangePattern ? $"({Pattern(a)})" : Pattern(a))),
        _ => throw new InvalidOperationException($"Unexpected pattern {pattern.GetType().Name}"),
    };

    /// <summary>
    /// Property patterns over the Option/Result structs: <c>Some(p)</c> → <c>{ IsSome: true, Value: p }</c>,
    /// <c>None</c> → <c>{ IsSome: false }</c>, <c>Ok(p)</c>/<c>Err(p)</c> likewise with IsOk and Value/Error.
    /// </summary>
    private string BuiltinPattern(BoundConstructorPattern pattern)
    {
        TypeName(pattern.Type);
        var variant = (VariantSymbol)pattern.Constructor;
        var (test, member) = variant.Name switch
        {
            "Some" => ("IsSome: true", "Value"),
            "None" => ("IsSome: false", null),
            "Ok" => ("IsOk: true", "Value"),
            _ => ("IsOk: false", "Error"),
        };
        return member is null || pattern.Arguments[0] is BoundWildcardPattern
            ? $"{{ {test} }}"
            : $"{{ {test}, {member}: {Pattern(pattern.Arguments[0])} }}";
    }

    /// <summary><c>Option&lt;int&gt;.Some</c> for a variant of <paramref name="type"/>, or the record type itself.</summary>
    private string ConstructorName(Symbol constructor, TypeSymbol type) => constructor switch
    {
        VariantSymbol variant => $"{TypeName(type)}.{Escape(variant.Name)}",
        RecordSymbol => TypeName(type),
        _ => throw new InvalidOperationException($"Unexpected constructor {constructor.GetType().Name}"),
    };

    private static BoundExpression Unwrap(BoundExpression expression) =>
        expression is BoundConversionExpression conversion ? Unwrap(conversion.Expression) : expression;

    // Names, types and literals

    /// <param name="shadowedNames">User type names hidden by a nested type at this point (enum variants); those are emitted with <c>global::</c>.</param>
    private string TypeName(TypeSymbol type, ISet<string>? shadowedNames = null)
    {
        if (type == TypeSymbol.Int) return "int";
        if (type == TypeSymbol.Long) return "long";
        if (type == TypeSymbol.Double) return "double";
        if (type == TypeSymbol.Bool) return "bool";
        if (type == TypeSymbol.String) return "string";
        if (type == TypeSymbol.Char) return "char";
        if (type == TypeSymbol.Byte) return "byte";
        if (type == TypeSymbol.SByte) return "sbyte";
        if (type == TypeSymbol.Short) return "short";
        if (type == TypeSymbol.UShort) return "ushort";
        if (type == TypeSymbol.UInt) return "uint";
        if (type == TypeSymbol.ULong) return "ulong";
        if (type == TypeSymbol.Float) return "float";
        if (type == TypeSymbol.Decimal) return "decimal";
        if (type == TypeSymbol.Unit) return "void";

        if (type.OriginalDefinition is EnumSymbol { BuiltinKind: not BuiltinEnumKind.None } builtin)
            _usedBuiltins.Add(builtin);

        // Types from another namespace (or hidden by a variant of the same name) are written in full.
        string Qualify(Symbol definition)
        {
            var name = Escape(definition.Name);
            var @namespace = definition.Namespace ?? RootOf(_program.FileNamespaces[0]);
            bool sameNamespace = @namespace == _namespace || (@namespace.IsGlobal && _namespace is { IsGlobal: true });
            return sameNamespace && shadowedNames?.Contains(definition.Name) != true
                ? name
                : $"global::{Qualified(@namespace, name)}";
        }

        string Arguments(IEnumerable<TypeSymbol> arguments) => string.Join(", ", arguments.Select(a => TypeName(a, shadowedNames)));

        return type switch
        {
            NamedTypeDefinition or TraitSymbol => Qualify(type),
            TupleTypeSymbol tuple => $"({string.Join(", ", tuple.ElementTypes.Select((e, i) =>
                tuple.ElementNames[i] is { } name ? $"{TypeName(e, shadowedNames)} {Escape(name)}" : TypeName(e, shadowedNames)))})",
            ConstructedTypeSymbol constructed => $"{Qualify(constructed.Definition)}<{Arguments(constructed.TypeArguments)}>",
            TypeParameterSymbol parameter => Escape(parameter.Name),
            ClrTypeSymbol { IsArray: true } array => ArrayTypeName(array, null, shadowedNames),
            ClrTypeSymbol clr when clr.TypeArguments.IsEmpty => ClrTypeName(clr.Type),
            ClrTypeSymbol clr => $"{ClrTypeName(clr.Type)}<{Arguments(clr.TypeArguments)}>",
            _ => throw new InvalidOperationException($"Type '{type}' cannot be emitted."),
        };
    }

    /// <summary>
    /// <c>int[]</c>, <c>int[,]</c>, or (with <paramref name="sizes"/>) the <c>int[5]</c> of <c>new int[5]</c>. C# writes the
    /// innermost element type first, then the rank specifiers outermost first: an array of <c>int[,]</c> is <c>int[][,]</c>.
    /// </summary>
    private string ArrayTypeName(ClrTypeSymbol array, string? sizes, ISet<string>? shadowedNames = null)
    {
        var ranks = new StringBuilder(sizes is null ? "" : $"[{sizes}]");
        TypeSymbol element = array;
        for (bool outermost = true; element is ClrTypeSymbol { IsArray: true } current; outermost = false)
        {
            if (!(outermost && sizes is not null))
                ranks.Append('[').Append(',', current.Rank - 1).Append(']');
            element = current.TypeArguments[0];
        }
        return TypeName(element, shadowedNames) + ranks;
    }

    /// <summary><c>global::System.Text.StringBuilder</c>; for a generic definition the name without its arity.</summary>
    private string ClrTypeName(Type type)
    {
        foreach (var (piper, clr) in ClrTypes.BuiltinTypes)
        {
            if (clr == type)
                return TypeName(piper);
        }
        if (type == typeof(object))
            return "object";
        return $"global::{type.Namespace}.{type.Name.Split('`')[0]}";
    }

    private static string Escape(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

    private static string Literal(object value) => value switch
    {
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture) + "L",
        uint u => u.ToString(CultureInfo.InvariantCulture) + "u",
        // Only as pattern constants, where C# converts the integer literal to the matched type.
        byte or sbyte or short or ushort => System.Convert.ToString(value, CultureInfo.InvariantCulture)!,
        ulong u => u.ToString(CultureInfo.InvariantCulture) + "UL",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
        decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
        double d => DoubleLiteral(d),
        bool b => b ? "true" : "false",
        char c => c == '\'' ? "'\\''" : c == '"' ? "'\"'" : $"'{EscapeString(c.ToString(), interpolated: false)}'",
        string s => $"\"{EscapeString(s, interpolated: false)}\"",
        _ => throw new InvalidOperationException($"Unexpected literal {value.GetType().Name}"),
    };

    private static string DoubleLiteral(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private static string EscapeString(string text, bool interpolated)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append(@"\n"); break;
                case '\r': builder.Append(@"\r"); break;
                case '\t': builder.Append(@"\t"); break;
                case '\0': builder.Append(@"\0"); break;
                case '{' when interpolated: builder.Append("{{"); break;
                case '}' when interpolated: builder.Append("}}"); break;
                case < ' ': builder.Append($"\\u{(int)c:X4}"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>Gives each variable in a method a unique C# name that doesn't collide with top-level names.</summary>
    private sealed class NameAllocator(IEnumerable<string> reserved)
    {
        private readonly HashSet<string> _used = [.. reserved];
        private readonly Dictionary<VariableSymbol, string> _names = [];

        public string Declare(VariableSymbol variable)
        {
            var name = variable.Name;
            for (int n = 1; !_used.Add(name); n++)
                name = $"{variable.Name}_{n}";
            _names[variable] = name;
            return name;
        }

        public string Get(VariableSymbol variable) => _names.TryGetValue(variable, out var name) ? name : Declare(variable);
    }
}
