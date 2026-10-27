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
public sealed partial class CSharpEmitter
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


    /// <summary>The helpers where .NET's null meets Option, in the Piper.Runtime assembly.</summary>
    public const string InteropClassName = "Piper.Runtime.PiperInterop";

    private readonly BoundProgram _program;
    private readonly Dictionary<MethodSymbol, BoundFunction> _methodBodies;
    private readonly HashSet<string> _topLevelNames;
    private readonly Dictionary<SourceText, string> _moduleClassNames = [];
    private readonly Dictionary<SourceText, string> _extensionClassNames = [];
    private readonly Dictionary<(SourceText File, TypeSymbol Type), string> _separateExtensionClasses = [];
    private readonly SourceText _runtimeFile;
    private CodeWriter _writer = new();
    private NameAllocator _names = new([]);

    /// <summary>Label names, unique across the file (C# labels have their own namespace, so they never clash with variables).</summary>
    private readonly Dictionary<BoundLabel, string> _labels = [];

    /// <summary>How <c>this</c> is spelled in the method being emitted: <c>this</c> in members, the receiver parameter in extension blocks.</summary>
    private string _thisName = "this";

    /// <summary>The namespace whose block is being written; types elsewhere are written fully qualified.</summary>
    private NamespaceSymbol? _namespace;

    /// <summary>Set while writing a file's module class: that file's functions are called unqualified.</summary>
    private SourceText? _moduleFile;


    /// <summary>Whether to write `#line` directives mapping the C# back to the Piper source.</summary>
    private readonly bool _lineDirectives;

    /// <summary>The Piper file whose code is being written (null in runtime support), and the last line mapped to.</summary>
    private SourceText? _lineFile;
    private int _lastLine = -1;

    /// <summary>Extern aliases for referenced assemblies whose types C# can only name by alias (see <see cref="ProgramReferences.Aliases"/>).</summary>
    private readonly IReadOnlyDictionary<string, string> _aliases;

    private CSharpEmitter(BoundProgram program, bool lineDirectives, IReadOnlyDictionary<string, string> aliases)
    {
        _program = program;
        _lineDirectives = lineDirectives;
        _aliases = aliases;
        _methodBodies = program.Methods.ToDictionary(m => (MethodSymbol)m.Symbol);
        _topLevelNames =
        [
            .. program.Types.Select(t => t.Name), .. program.BuiltinTypes.Select(t => t.Name),
            .. program.Functions.Select(f => f.Symbol.Name), EntryPointClassName,
            // The entry point's own Main method would hide a module class of that name.
            "Main",
        ];
        foreach (var file in program.Files)
        {
            var moduleName = UniqueTopLevelName(ModuleName(file.FileName), "Module");
            _moduleClassNames[file] = moduleName;
            _extensionClassNames[file] = UniqueTopLevelName(moduleName + "Extensions", "_");

            // Static extension members are plain static methods of their class, so one name on two types would clash.
            var staticNames = new HashSet<string>();
            foreach (var group in program.Methods.Select(m => (MethodSymbol)m.Symbol)
                         .Where(m => m.DeclaringFile == file && m.IsStatic && m.ContainingType is not (TraitSymbol or NamedTypeDefinition { IsRuntimeType: false }))
                         .GroupBy(m => m.ContainingType))
            {
                if (group.Any(m => staticNames.Contains(m.Name)))
                    _separateExtensionClasses[(file, group.Key)] = UniqueTopLevelName($"{_extensionClassNames[file]}_{new string([.. group.Key.Name.Where(char.IsAsciiLetterOrDigit)])}", "_");
                staticNames.UnionWith(group.Select(m => m.Name));
            }
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
    /// <param name="aliases">Extern aliases for referenced assemblies (assembly name → alias), whose types are named by them.</param>
    public static ImmutableArray<GeneratedFile> Emit(BoundProgram program, bool lineDirectives = false, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var emitter = new CSharpEmitter(program, lineDirectives, aliases ?? ImmutableDictionary<string, string>.Empty);
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
        foreach (var alias in _aliases.Values.Order(StringComparer.Ordinal))
            _writer.WriteLine($"extern alias {alias};");
        _writer.WriteLine("#nullable enable");
        // Option<T>, Result<T, E> and PiperInterop come from the Piper.Runtime assembly, shared by all Piper code.
        _writer.WriteLine("using Piper.Runtime;");
        // C#'s warnings about the generated code aren't actionable (Piper reports its own), and would break builds with
        // TreatWarningsAsErrors; using an obsolete .NET API is still worth a warning, at the Piper line.
        _writer.WriteLine("#pragma warning disable");
        _writer.WriteLine("#pragma warning restore CS0612, CS0618");
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
                    ClassSymbol @class => () => EmitClass(@class, file),
                    EnumSymbol @enum => () => EmitEnum(@enum, file),
                    TraitSymbol trait => () => EmitTrait(trait, file),
                    _ => throw new InvalidOperationException(),
                });
            }
            foreach (var type in contributions.Where(t => NamespaceOf(t) == @namespace))
                sections.Add(() => EmitPartialPart(type, file));

            var functions = _program.Functions.Where(f => f.Symbol.DeclaringFile == file).ToList();
            var moduleFunctions = functions.Where(f => !InFileClass(f.Symbol)).ToList();
            var fileFunctions = functions.Where(f => InFileClass(f.Symbol)).ToList();
            if (moduleFunctions.Count > 0 || file == _runtimeFile)
                sections.Add(() => EmitModuleClass(file, moduleFunctions, fileClass: false));
            if (fileFunctions.Count > 0)
                sections.Add(() => EmitModuleClass(file, fileFunctions, fileClass: true));
            if (BoxedTypesIn(file).Count > 0)
                sections.Add(() => EmitBoxes(file));
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

    /// <summary>
    /// A private function whose signature mentions a private type (a C# `file` type) can't be a member of the public
    /// module class, as C# allows file types only in file types' signatures, so it goes in a `file` class of its own.
    /// </summary>
    private static bool InFileClass(FunctionSymbol function) =>
        function.IsPrivate && (Piper.Binding.Binder.MentionsPrivateType(function.ReturnType) || function.Parameters.Any(p => Piper.Binding.Binder.MentionsPrivateType(p.Type)));

    private string ModuleClassName(SourceText file, bool fileClass) =>
        Escape(_moduleClassNames[file] + (fileClass ? "_Private" : ""));

    /// <summary>Whether a call to <paramref name="function"/> from the class being written needs no qualifier.</summary>
    private bool InCurrentClass(FunctionSymbol function) => function.DeclaringFile == _moduleFile && InFileClass(function) == _inFileClass;

    private bool _inFileClass;

    private void EmitModuleClass(SourceText file, List<BoundFunction> functions, bool fileClass)
    {
        // Public only when it holds a public function, so a library exports no empty class.
        var access = fileClass ? "file" : functions.Any(f => f.Symbol.IsExported) ? "public" : "internal";
        _writer.WriteLine($"{access} static class {ModuleClassName(file, fileClass)}");
        _writer.OpenBrace();
        _moduleFile = file;
        _inFileClass = fileClass;
        for (int i = 0; i < functions.Count; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            EmitFunction(functions[i], functions[i].Symbol.IsExported ? "public static " : "internal static ");
        }
        _moduleFile = null;
        _inFileClass = false;
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
    private SourceText HomeFile(GenericTypeDefinition type) => type.DeclaringFile ?? _runtimeFile;

    /// <summary>A record's or enum's methods; a trait's methods and overrides, including those added by `extend Trait`.</summary>
    private static IEnumerable<MethodSymbol> MethodsOf(GenericTypeDefinition type) => type switch
    {
        TraitSymbol trait => trait.Methods.Concat(trait.Overrides),
        NamedTypeDefinition definition => definition.Methods,
        _ => [],
    };

    private IEnumerable<MethodSymbol> MethodsFrom(GenericTypeDefinition type, SourceText file) =>
        MethodsOf(type).Where(m => (m.DeclaringFile ?? _runtimeFile) == file);

    private IEnumerable<TypeSymbol> TraitsFrom(GenericTypeDefinition type, SourceText file) =>
        type is NamedTypeDefinition definition
            ? definition.Conformances.Where(c => (c.File ?? _runtimeFile) == file).Select(c => c.Trait)
            : [];

    /// <summary>Other files add to this type with extend blocks, so every C# part must be <c>partial</c>.</summary>
    private bool IsPartial(GenericTypeDefinition type) =>
        MethodsOf(type).Any(m => (m.DeclaringFile ?? _runtimeFile) != HomeFile(type)) ||
        (type is NamedTypeDefinition definition && definition.Conformances.Any(c => (c.File ?? _runtimeFile) != HomeFile(type)));

    private IEnumerable<GenericTypeDefinition> ForeignContributions(SourceText file) =>
        _program.Types.OfType<GenericTypeDefinition>()
            .Where(t => HomeFile(t) != file && (MethodsFrom(t, file).Any() || TraitsFrom(t, file).Any()));

    /// <summary>A type's C# access: <c>file</c> for private, <c>public</c> when exported, otherwise <c>internal</c>.</summary>
    private static string Visibility(Symbol symbol) => symbol.IsPrivate ? "file" : symbol.IsExported ? "public" : "internal";

    /// <summary>
    /// <c>public sealed record Pair&lt;A, B&gt;(A first, B second) : Trait where A : Bound</c>, with a body
    /// holding any extend methods (which is what lets the record implement trait interfaces).
    /// </summary>
    private void EmitRecord(RecordSymbol record, SourceText file)
    {
        var partial = IsPartial(record) ? "partial " : "";
        var header = $"{Visibility(record)} sealed {partial}record {Escape(record.Name)}{TypeParameterList(record.TypeParameters)}" +
                     $"({RecordFields(record)}){BaseList(TraitsFrom(record, file))}{Constraints(record.TypeParameters)}";
        var methods = MethodsFrom(record, file).ToList();
        bool json = !record.Invariants.IsEmpty && !HasJsonConverter(record);
        EmitDocumentation(record, record.Fields);
        EmitAttributes(record);
        if (json)
        {
            // A generic record's converter is closed with its type arguments by the runtime, as an attribute can't.
            var converter = record.TypeParameters.IsEmpty ? $"{Escape(record.Name)}.{JsonConverterName}" : "global::Piper.Runtime.PiperRecordJsonConverterFactory";
            _writer.WriteLine($"[global::System.Text.Json.Serialization.JsonConverter(typeof({converter}))]");
        }
        if (methods.Count == 0 && record.Invariants.IsEmpty)
        {
            _writer.WriteLine(header + ";");
            return;
        }

        _writer.WriteLine(header);
        _writer.OpenBrace();
        if (!record.Invariants.IsEmpty)
        {
            EmitInvariants(record);
            if (methods.Count > 0 || json)
                _writer.WriteLine();
        }
        if (json)
        {
            EmitRecordJson(record);
            if (methods.Count > 0)
                _writer.WriteLine();
        }
        EmitMembers(methods);
        _writer.CloseBrace();
    }

    /// <summary>Whether a type chooses its own JSON converter, which then replaces Piper's.</summary>
    private static bool HasJsonConverter(Symbol type) =>
        type.Attributes.Any(a => a.Type is ClrTypeSymbol { Type.FullName: "System.Text.Json.Serialization.JsonConverterAttribute" });

    private const string JsonConverterName = "PiperJsonConverter";

    /// <summary>
    /// For a record with invariants, a converter making JSON they reject bad input (a <c>JsonException</c>, so a 400
    /// from ASP.NET Core) rather than a failure (an <c>ArgumentException</c>). System.Text.Json reads and writes a
    /// private copy of the record's fields, with their attributes and defaults but no invariants, so everything it does
    /// for a record (names, attributes, options) still applies; the converter only makes the record from it.
    /// </summary>
    private void EmitRecordJson(RecordSymbol record)
    {
        const string json = "global::System.Text.Json";
        var type = $"{Escape(record.Name)}{TypeParameterList(record.TypeParameters)}";
        var fields = record.Fields.Select(f => Escape(f.Name)).ToList();
        var shapeFields = string.Join(", ", record.Fields.Select(f => $"{InlineAttributes(f)}{TypeName(f.Type)} {Escape(f.Name)}{DefaultValue(f.Default)}"));

        _writer.WriteLine($"private sealed record PiperJsonShape({shapeFields});");
        _writer.WriteLine();
        _writer.WriteLine($"public sealed class {JsonConverterName} : {json}.Serialization.JsonConverter<{type}>");
        _writer.OpenBrace();
        _writer.WriteLine($"public override {type}? Read(ref {json}.Utf8JsonReader reader, global::System.Type type, {json}.JsonSerializerOptions options)");
        _writer.OpenBrace();
        _writer.WriteLine($"if ({json}.JsonSerializer.Deserialize<PiperJsonShape>(ref reader, options) is not {{ }} shape)");
        _writer.WriteLine("    return null;");
        _writer.WriteLine("try");
        _writer.OpenBrace();
        _writer.WriteLine($"return new {type}({string.Join(", ", fields.Select(f => $"shape.{f}"))});");
        _writer.CloseBrace();
        _writer.WriteLine("catch (global::System.ArgumentException invalid)");
        _writer.OpenBrace();
        _writer.WriteLine($"throw new {json}.JsonException(invalid.Message, invalid);");
        _writer.CloseBrace();
        _writer.CloseBrace();
        _writer.WriteLine();
        _writer.WriteLine($"public override void Write({json}.Utf8JsonWriter writer, {type} value, {json}.JsonSerializerOptions options) =>");
        _writer.WriteLine($"    {json}.JsonSerializer.Serialize(writer, new PiperJsonShape({string.Join(", ", fields.Select(f => $"value.{f}"))}), options);");
        _writer.CloseBrace();
    }

    /// <summary>
    /// <c>public abstract partial class Shape(string name) : Base(name), Named</c>, with its fields (private ones as
    /// fields, public ones as properties) and methods. Its primary constructor is C# 12's: the parameters are usable in
    /// the initializers, the base's arguments and the methods.
    /// </summary>
    private void EmitClass(ClassSymbol @class, SourceText file)
    {
        var modifiers = @class.IsAbstract ? "abstract " : @class.IsSealed ? "sealed " : "";
        // Base arguments and other constructors need a primary constructor, so a class with either has one, if only an
        // empty one. So does one whose base constructor sets the required members it inherits, to say it does too.
        var parameters = @class.Parameters.IsEmpty && @class.BaseArguments.IsEmpty && @class.Constructors.IsEmpty && !@class.SetsRequiredMembers
            ? ""
            : $"({string.Join(", ", @class.Parameters.Select(p => $"{TypeName(p.Type)} {Escape(p.Name)}{DefaultValue(p.Default)}"))})";
        _names = new NameAllocator(@class.Parameters.Select(p => p.Name));

        var bases = new List<string>();
        if (@class.Base is { } @base)
            bases.Add(TypeName(@base) + (@class.BaseArguments.IsEmpty ? "" : $"({Arguments(@class.BaseArguments)})"));
        bases.AddRange(TraitsFrom(@class, file).Select(t => TypeName(t)));
        var baseList = bases.Count == 0 ? "" : $" : {string.Join(", ", bases)}";

        EmitDocumentation(@class, @class.Parameters);
        EmitAttributes(@class);
        if (@class.SetsRequiredMembers)
            _writer.WriteLine($"[method: {SetsRequiredMembers}]");
        _writer.WriteLine($"{Visibility(@class)} {modifiers}partial class {Escape(@class.Name)}{TypeParameterList(@class.TypeParameters)}{parameters}{baseList}{Constraints(@class.TypeParameters)}");
        _writer.OpenBrace();
        // Its invariants are checked before anything else runs: in its first field's initializer, as instance field
        // initializers run before the base constructor (and before the base arguments are evaluated).
        var checks = string.Join(" && ", @class.Invariants.Select(contract =>
            $"global::Piper.Runtime.PiperContract.Invariant({Expr(contract.Condition, AssignmentPrecedence)}, {Literal(contract.Text)}, " +
            $"{Expr(Piper.Binding.Binder.ContractValues(contract), AssignmentPrecedence)})"));
        var firstStored = @class.Fields.FirstOrDefault(f => f.Initializer is not null);
        if (checks.Length > 0 && firstStored is null)
            _writer.WriteLine($"private readonly bool __PiperInvariants = {checks};");
        // What stores a value first (fields and automatic properties), static ones first, in order: C# runs their
        // initializers in the order they're written.
        var stored = @class.StaticFields.Concat(@class.Fields).Where(f => f.Initializer is not null).ToList();
        foreach (var field in stored)
        {
            var type = TypeName(field.Type);
            var @static = field.IsStatic ? "static " : "";
            var initializer = Expr(field.Initializer!, AssignmentPrecedence);
            if (checks.Length > 0 && field == firstStored)
                initializer = $"{checks} ? ({initializer}) : default!";
            if (field.IsProperty)
                EmitDocumentation(field, []);
            EmitAttributes(field);
            // A public field is a C# property, as are automatic properties; private and protected fields are C# fields.
            var access = field.IsPublic ? "public " : field.IsProtected ? "protected " : "private ";
            var keyword = field.IsInitOnly ? "init" : "set";
            var set = !field.IsMutable ? "" : field.HasPrivateSetter ? $" private {keyword};" : field.HasProtectedSetter ? $" protected {keyword};" : $" {keyword};";
            _writer.WriteLine(field.IsPublic || field.IsProperty
                ? $"{access}{@static}{type} {Escape(field.Name)} {{ get;{set} }} = {initializer};"
                : $"{access}{@static}{(field.IsMutable ? "" : "readonly ")}{type} {Escape(field.Name)} = {initializer};");
        }
        // Then properties with accessors, indexers, and methods.
        var properties = @class.StaticFields.Concat(@class.Fields).Where(f => f.IsProperty && f.Initializer is null).ToList();
        // A property an extend block adds to the class has no field; it's emitted with the methods.
        var methods = MethodsFrom(@class, file).Where(m => !m.IsHidden || (PropertyOf(m) is not null && !@class.Fields.Concat(@class.StaticFields).Any(f => f.Getter == m))).ToList();
        bool any = stored.Count > 0;
        foreach (var property in properties)
        {
            if (any)
                _writer.WriteLine();
            EmitProperty(property);
            any = true;
        }
        foreach (var indexer in @class.Indexers)
        {
            if (any)
                _writer.WriteLine();
            EmitIndexer(indexer);
            any = true;
        }
        foreach (var constructor in @class.Constructors)
        {
            if (any)
                _writer.WriteLine();
            EmitConstructor(@class, constructor);
            any = true;
        }
        if (methods.Count > 0)
        {
            if (any)
                _writer.WriteLine();
            EmitMembers(methods);
        }
        foreach (var bridge in @class.PropertyBridges)
        {
            _writer.WriteLine();
            EmitPropertyBridge(bridge);
        }
        foreach (var bridge in @class.IndexerBridges)
        {
            _writer.WriteLine();
            EmitIndexerBridge(bridge);
        }
        _writer.CloseBrace();
    }

    /// <summary>
    /// <c>string? IStore.this[string? key] { get =&gt; PiperInterop.ToNullableReference(this[PiperInterop.FromNullableReference(key)]); set =&gt; … }</c>:
    /// implements a .NET interface indexer that may take or give null with the class's indexer of Options.
    /// </summary>
    private void EmitIndexerBridge(ClrIndexerBridge bridge)
    {
        static string Kind(ClrBoundary boundary) => boundary == ClrBoundary.NullableReference ? "NullableReference" : "NullableValue";
        string CSharpType(TypeSymbol type, ClrBoundary boundary) => TypeName(type) + (boundary == ClrBoundary.None ? "" : "?");
        string FromNullable(string value, ClrBoundary boundary) =>
            boundary == ClrBoundary.None ? value : $"global::{InteropClassName}.From{Kind(boundary)}({value})";

        // Named as the interface's parameters, which don't clash with `value`.
        var names = bridge.Parameters.Select((_, i) => $"piperIndex{i}").ToList();
        var parameters = string.Join(", ", bridge.Parameters.Select((p, i) => $"{CSharpType(p.Type, p.Boundary)} {names[i]}"));
        var element = $"this[{string.Join(", ", bridge.Parameters.Select((p, i) => FromNullable(names[i], p.Boundary)))}]";
        var get = bridge.Boundary == ClrBoundary.None ? element : $"global::{InteropClassName}.To{Kind(bridge.Boundary)}({element})";
        var header = $"{CSharpType(bridge.Type, bridge.Boundary)} {TypeName(bridge.Interface)}.this[{parameters}]";
        _writer.WriteLine(bridge.Settable
            ? $"{header} {{ get => {get}; set => {element} = {FromNullable("value", bridge.Boundary)}; }}"
            : $"{header} => {get};");
    }

    /// <summary>
    /// <c>object? IAsyncResult.AsyncState =&gt; PiperInterop.ToNullableReference(this.AsyncState);</c>: implements a .NET
    /// interface property that may be null with the class's Option property.
    /// </summary>
    private void EmitPropertyBridge(ClrPropertyBridge bridge)
    {
        var kind = bridge.Boundary == ClrBoundary.NullableReference ? "NullableReference" : "NullableValue";
        var name = Escape(bridge.Property.Name);
        var get = $"global::{InteropClassName}.To{kind}(this.{name})";
        var header = $"{TypeName(bridge.Type)}? {TypeName(bridge.Interface)}.{name}";
        _writer.WriteLine(bridge.Settable
            ? $"{header} {{ get => {get}; set => this.{name} = global::{InteropClassName}.From{kind}(value); }}"
            : $"{header} => {get};");
    }

    /// <summary>
    /// A class's property with accessors: <c>public decimal Total =&gt; price * quantity;</c> for a getter alone,
    /// otherwise <c>public string Name { get =&gt; …; private set { … } }</c>. An abstract one's accessors have no bodies.
    /// </summary>
    private void EmitProperty(FieldSymbol property)
    {
        if ((property.Getter ?? property.Setter) is not { } accessor)
            return;
        _thisName = "this";
        EmitDocumentation(property, []);
        EmitAttributes(property);
        var header = $"{AccessorModifiers(accessor)}{(property.IsStatic ? "static " : "")}{TypeName(property.Type)} {Escape(property.Name)}";
        EmitAccessors(header, property.Getter, property.Setter);
    }

    /// <summary><c>public string this[int index] { get =&gt; …; set { … } }</c>, its parameters named as its accessors name them.</summary>
    private void EmitIndexer(IndexerSymbol indexer)
    {
        _thisName = "this";
        EmitAttributes(indexer);
        _names = NewNameAllocator(indexer.Getter);
        var parameters = string.Join(", ", indexer.Parameters.Select(p => $"{TypeName(p.Type)} {Escape(_names.Get(p))}"));
        var header = $"{AccessorModifiers(indexer.Getter)}{TypeName(indexer.Type)} this[{parameters}]";
        EmitAccessors(header, indexer.Getter, indexer.Setter);
    }

    /// <summary>
    /// A property's or indexer's access, from its getter (or its setter, if it has no getter), and modifiers. An
    /// override of a protected one is protected too.
    /// </summary>
    private static string AccessorModifiers(MethodSymbol accessor)
    {
        var access = accessor.IsPrivate ? "private "
            : accessor.Overridden is MethodInfo { IsFamilyOrAssembly: true } ? "protected internal "
            : accessor.IsProtected ? "protected "
            : "public ";
        return access + (accessor.IsOverride ? "override " : accessor.IsAbstract ? "abstract " : accessor.IsVirtual ? "virtual " : "");
    }

    /// <summary>
    /// After <paramref name="header"/>, the accessors: none with bodies for an abstract one, <c>=&gt; expression;</c> for
    /// a getter alone written as one, otherwise <c>{ get … set … }</c>. A setter more restricted than the getter says so
    /// (<c>private set</c>, <c>protected set</c>).
    /// </summary>
    private void EmitAccessors(string header, MethodSymbol? getter, MethodSymbol? setter)
    {
        var keyword = setter is { IsInitAccessor: true } ? "init" : "set";
        var set = setter is null ? null
            : getter is null ? keyword
            : setter.IsPrivate && !getter.IsPrivate ? $"private {keyword}"
            : setter.IsProtected && !getter.IsProtected && !getter.IsPrivate ? $"protected {keyword}"
            : keyword;
        if ((getter ?? setter)!.IsAbstract)
        {
            _writer.WriteLine($"{header} {{ get;{(set is null ? "" : $" {set};")} }}");
            return;
        }
        // A getter alone, written as an expression: `public decimal Total => price * quantity;`.
        if (setter is null && getter is not null && _methodBodies[getter] is { IsExpressionBodied: true } function &&
            ExpressionBody((BoundBlockStatement)function.Body) is not null)
        {
            EmitAccessor(getter, header);
            return;
        }
        _writer.WriteLine(header);
        _writer.OpenBrace();
        if (getter is not null)
            EmitAccessor(getter, "get");
        if (setter is not null)
            EmitAccessor(setter, set!);
        _writer.CloseBrace();
    }

    /// <summary>Marks a class's constructors as setting the .NET required members it inherits, as its base constructor does.</summary>
    private const string SetsRequiredMembers = "global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers";

    /// <summary><c>public Account(string owner) : this(owner, 0m) { … }</c>: a constructor besides the primary one.</summary>
    private void EmitConstructor(ClassSymbol @class, ClassConstructor constructor)
    {
        var method = constructor.Method;
        _names = NewNameAllocator(method);
        _thisName = "this";
        var access = method.IsPrivate ? "private " : method.IsProtected ? "protected " : "public ";
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{TypeName(p.Type)} {Escape(_names.Get(p))}{DefaultValue(p.Default)}"));
        EmitDocumentation(method, method.Parameters);
        EmitAttributes(method);
        if (@class.SetsRequiredMembers)
            _writer.WriteLine($"[{SetsRequiredMembers}]");
        LineDirective(method.Declaration!.Span);
        try
        {
            _writer.WriteLine($"{access}{Escape(@class.Name)}({parameters}) : this({Arguments(constructor.ThisArguments, constructor.ArgumentNames)})");
            _lastLine = -1;
            EmitBlock((BoundBlockStatement)_methodBodies[method].Body);
        }
        finally
        {
            if (_lineDirectives && _lastLine >= 0)
            {
                _writer.WriteLine("#line default");
                _lastLine = -1;
            }
        }
    }

    /// <summary><c>get =&gt; name;</c> or <c>set { … }</c>: an accessor's body, after <paramref name="head"/>.</summary>
    private void EmitAccessor(MethodSymbol accessor, string head)
    {
        var function = _methodBodies[accessor];
        _names = NewNameAllocator(accessor);
        LineDirective(accessor.Declaration!.Span);
        try
        {
            var body = (BoundBlockStatement)function.Body;
            if (function.IsExpressionBodied && ExpressionBody(body) is { } expression)
            {
                _writer.WriteLine($"{head} => {Expr(expression, AssignmentPrecedence)};");
                return;
            }
            _writer.WriteLine(head);
            _lastLine = -1;
            EmitBlock(body);
        }
        finally
        {
            if (_lineDirectives && _lastLine >= 0)
            {
                _writer.WriteLine("#line default");
                _lastLine = -1;
            }
        }
    }

    /// <summary>
    /// A record's `requires` conditions, checked on construction. Its properties are redeclared, in order (so
    /// printing and deconstruction keep the fields' order), and the first one's initializer checks the conditions
    /// before giving it its value: property initializers run in the primary constructor and can read all its parameters.
    /// </summary>
    private void EmitInvariants(RecordSymbol record)
    {
        _names = new NameAllocator([]);
        foreach (var field in record.InvariantFields)
            _names.Declare(field);
        var first = record.Fields[0];
        var value = Escape(first.Name);
        foreach (var contract in record.Invariants)
        {
            var values = Expr(Piper.Binding.Binder.ContractValues(contract), AssignmentPrecedence);
            value = $"global::Piper.Runtime.PiperContract.Invariant({value}, {Expr(contract.Condition, AssignmentPrecedence)}, {Literal(contract.Text)}, {values})";
        }
        foreach (var field in record.Fields)
        {
            var attributes = string.Concat(field.Attributes.Where(a => a.Target == "property").Select(a => $"[{Attribute(a with { Target = null })}] "));
            var initializer = field == first ? value : Escape(field.Name);
            _writer.WriteLine($"{attributes}public {TypeName(field.Type)} {Escape(field.Name)} {{ get; init; }} = {initializer};");
        }
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

        EmitDocumentation(@enum, []);
        EmitAttributes(@enum);
        // In JSON a variant is its name, or an object naming it in "kind", unless the enum says otherwise.
        if (!HasJsonConverter(@enum))
            _writer.WriteLine("[global::System.Text.Json.Serialization.JsonConverter(typeof(global::Piper.Runtime.PiperEnumJsonConverterFactory))]");
        _writer.WriteLine($"{Visibility(@enum)} abstract {partial}record {name}{TypeParameterList(@enum.TypeParameters)}" +
                          $"{BaseList(TraitsFrom(@enum, file))}{Constraints(@enum.TypeParameters)}");
        _writer.OpenBrace();
        _writer.WriteLine($"private {name}()");
        _writer.OpenBrace();
        _writer.CloseBrace();
        foreach (var variant in @enum.Variants)
        {
            _writer.WriteLine();
            EmitDocumentation(variant, variant.Fields);
            var header = $"public sealed record {Escape(variant.Name)}({Fields(variant.Fields, variantNames)}) : {selfType}";
            // A fieldless variant prints like a C# enum member (`Food`), not as an empty record (`Food { }`), unless the
            // enum has its own ToString.
            _writer.WriteLine(variant.Fields.IsEmpty && !@enum.Methods.Any(m => m.Name == "ToString")
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
    private void EmitPartialPart(GenericTypeDefinition type, SourceText file)
    {
        var keyword = type switch
        {
            TraitSymbol => "partial interface",
            ClassSymbol => "partial class",
            EnumSymbol { BuiltinKind: not BuiltinEnumKind.None } => "readonly partial record struct",
            _ => "partial record",
        };
        _writer.WriteLine($"{keyword} {Escape(type.Name)}{TypeParameterList(type.TypeParameters)}{BaseList(TraitsFrom(type, file))}");
        _writer.OpenBrace();
        if (type is TraitSymbol)
            EmitTraitMembers(MethodsFrom(type, file).ToList());
        else
            EmitMembers(MethodsFrom(type, file).ToList());
        _writer.CloseBrace();
    }

    /// <summary>A trait is an interface; methods with a body become default interface methods.</summary>
    private void EmitTrait(TraitSymbol trait, SourceText file)
    {
        var partial = IsPartial(trait) ? "partial " : "";
        EmitDocumentation(trait, []);
        EmitAttributes(trait);
        _writer.WriteLine($"{Visibility(trait)} {partial}interface {Escape(trait.Name)}{TypeParameterList(trait.TypeParameters)}{BaseList(trait.Parents)}{Constraints(trait.TypeParameters)}");
        _writer.OpenBrace();
        var methods = MethodsFrom(trait, file).ToList();
        EmitTraitMembers(methods);
        if (methods.Count > 0)
            _writer.WriteLine();
        EmitWitnessTypes(trait);
        _writer.CloseBrace();
    }

    private void EmitTraitMembers(IReadOnlyList<MethodSymbol> methods)
    {
        bool first = true;
        for (int i = 0; i < methods.Count; i++)
        {
            var method = methods[i];
            // A settable property's setter is written with its getter, `string Name { get; set; }`.
            if (SetterOf(method) is not null)
                continue;
            if (!first)
                _writer.WriteLine();
            first = false;
            _thisName = "this";
            // A property: `double Area { get; }`, or with a default, `string Label => "shape";`. A static one is static
            // abstract, or with a default static virtual, as a static method is.
            if (PropertyOf(method) is { } property)
            {
                var @static = !method.IsStatic ? "" : _methodBodies.ContainsKey(method) ? "static virtual " : "static abstract ";
                var setter = methods.FirstOrDefault(m => SetterOf(m) == property);
                EmitDocumentation(method, []);
                EmitAccessors($"{@static}{TypeName(method.ReturnType)} {Escape(property)}", method, setter);
                continue;
            }
            // A static trait method is a C# 11 static abstract (or, with a body, static virtual) interface member.
            if (_methodBodies.TryGetValue(method, out var body))
            {
                EmitFunction(body, method.IsStatic ? "static virtual " : "");
            }
            else
            {
                _names = NewNameAllocator(method);
                EmitDocumentation(method, method.Parameters);
                EmitAttributes(method);
                _writer.WriteLine($"{Signature(method, method.IsStatic ? "static abstract " : "")};");
            }
        }
    }

    private void EmitMembers(IReadOnlyList<MethodSymbol> methods)
    {
        _thisName = "this";
        for (int i = 0; i < methods.Count; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            var method = methods[i];
            // A class's private method is private to it; an extend block's is visible to the program (it's private to
            // its file in Piper). An override of a protected .NET method must be protected too.
            var access = method.IsPrivate ? method.ContainingType is ClassSymbol ? "private " : "internal "
                : method.Overridden is MethodInfo { IsFamilyOrAssembly: true } ? "protected internal "
                : method.IsProtected ? "protected "
                : "public ";
            // An enum's ToString is sealed, so its variants (C# records deriving from it) don't generate their own.
            var modifier = method.ContainingType is EnumSymbol && method.IsOverride ? "sealed override "
                : method.ContainingType is not ClassSymbol ? method.IsOverride ? "override " : ""
                : method.IsOverride ? "override "
                : method.IsAbstract ? "abstract "
                : method.IsVirtual ? "virtual " : "";
            // An instance-form operator is a static C# operator whose first parameter, `self`, is Piper's `this`.
            _thisName = method.IsOperator ? "self" : "this";
            // A property an extend block adds: `public double Length => …;`.
            if (PropertyOf(method) is { } property)
            {
                EmitDocumentation(method, []);
                EmitAccessors($"{access}{(method.IsStatic ? "static " : "")}{modifier}{TypeName(method.ReturnType)} {Escape(property)}", method, null);
                continue;
            }
            if (method.ContainingType is ClassSymbol && method.IsAbstract)
            {
                _names = NewNameAllocator(method);
                _writer.WriteLine(Signature(method, access + modifier) + ";");
                continue;
            }
            EmitFunction(_methodBodies[method], access + (method.IsStatic ? "static " : "") + modifier);
        }
        _thisName = "this";

        var bridges = methods.Select(m => m.ContainingType).OfType<NamedTypeDefinition>().Distinct()
            .SelectMany(t => t.InterfaceBridges)
            .Where(b => methods.Contains(b.Method));
        foreach (var bridge in bridges)
        {
            _writer.WriteLine();
            EmitInterfaceBridge(bridge);
        }

        // Comparison operators for a type implementing IComparable<T> for itself, next to the method they call.
        foreach (var type in methods.Select(m => m.ContainingType).OfType<NamedTypeDefinition>().Distinct().Where(t => t.CompareTo is { } m && methods.Contains(m)))
        {
            var self = TypeName(type is GenericTypeDefinition generic ? generic.SelfType : type);
            _writer.WriteLine();
            foreach (var op in new[] { "<", "<=", ">", ">=" })
                _writer.WriteLine($"public static bool operator {op}({self} left, {self} right) => ((global::System.IComparable<{self}>)left).CompareTo(right) {op} 0;");
        }
    }

    /// <summary>
    /// <c>int IComparable&lt;Money&gt;.CompareTo(Money? other) =&gt; CompareTo(PiperInterop.FromNullableReference(other));</c>:
    /// implements a .NET interface method whose nullable parameters or result are Options in the Piper method.
    /// </summary>
    private void EmitInterfaceBridge(ClrInterfaceBridge bridge)
    {
        string CSharpType(TypeSymbol type, ClrBoundary boundary) =>
            type == TypeSymbol.Unit ? "void" : TypeName(type) + (boundary == ClrBoundary.None ? "" : "?");
        string Convert(string value, ClrBoundary boundary, string direction) =>
            boundary switch
            {
                ClrBoundary.NullableReference => $"global::{InteropClassName}.{direction}NullableReference({value})",
                ClrBoundary.NullableValue => $"global::{InteropClassName}.{direction}NullableValue({value})",
                _ => value,
            };

        var method = bridge.Method;
        var names = method.Parameters.Select(p => Escape(p.Name)).ToList();
        var parameters = bridge.Parameters.Select((p, i) => $"{CSharpType(p.Type, p.Boundary)} {names[i]}");
        var arguments = bridge.Parameters.Select((p, i) => Convert(names[i], p.Boundary, "From"));
        var call = Convert($"{Escape(method.Name)}({string.Join(", ", arguments)})", bridge.ReturnBoundary, "To");
        _writer.WriteLine($"{CSharpType(bridge.ReturnType, bridge.ReturnBoundary)} {TypeName(bridge.Interface)}.{Escape(method.Name)}({string.Join(", ", parameters)}) => {call};");
    }

    /// <summary>
    /// Methods on builtin and .NET types, and on Option and Result (which live in Piper.Runtime), become C# 14 extension
    /// members: <c>extension(int self) { public int squared() =&gt; self * self; }</c>, <c>extension&lt;T&gt;(Option&lt;T&gt; self)</c>.
    /// </summary>
    private IEnumerable<MethodSymbol> ExtensionMethods(SourceText file) => _program.Methods
        .Select(m => (MethodSymbol)m.Symbol)
        .Where(m => m.DeclaringFile == file && m.ContainingType is not (TraitSymbol or NamedTypeDefinition { IsRuntimeType: false }));

    private bool HasExtensions(SourceText file) => ExtensionMethods(file).Any();

    private void EmitExtensions(SourceText file)
    {
        var classes = ExtensionMethods(file).GroupBy(m => ExtensionClassName(m)).ToList();
        for (int c = 0; c < classes.Count; c++)
        {
            if (c > 0)
                _writer.WriteLine();
            var groups = classes[c].GroupBy(m => (m.ContainingType, m.IsStatic)).ToList();
            // Public only when a `public extend` block adds to it.
            _writer.WriteLine($"{(classes[c].Any(m => m.IsExported) ? "public" : "internal")} static class {Escape(classes[c].Key)}");
            _writer.OpenBrace();
            for (int i = 0; i < groups.Count; i++)
            {
                if (i > 0)
                    _writer.WriteLine();
                var (type, isStatic) = groups[i].Key;
                // A generic type is extended for all its instantiations: `extension<T>(Option<T> self)`.
                var (generics, extended) = type is NamedTypeDefinition definition
                    ? (TypeParameterList(definition.TypeParameters), TypeName(definition.SelfType))
                    : ("", TypeName(type));
                _writer.WriteLine(isStatic ? $"extension{generics}({extended})" : $"extension{generics}({extended} self)");
                _writer.OpenBrace();
                var methods = groups[i].ToList();
                for (int j = 0; j < methods.Count; j++)
                {
                    if (j > 0)
                        _writer.WriteLine();
                    _thisName = "self";
                    var access = methods[j].IsExported ? "public " : "internal ";
                    // A C# 14 extension property: `public bool IsEven => self % 2 == 0;`.
                    if (PropertyOf(methods[j]) is { } property)
                        EmitAccessors($"{access}{(isStatic ? "static " : "")}{TypeName(methods[j].ReturnType)} {Escape(property)}", methods[j], null);
                    else
                        EmitFunction(_methodBodies[methods[j]], access + (isStatic ? "static " : ""));
                }
                _writer.CloseBrace();
            }
            _writer.CloseBrace();
        }
        _thisName = "this";
    }

    /// <summary>
    /// The class holding an extension member: the file's extensions class, except that static methods of the same name on
    /// two types (`int.empty()`, `string.empty()`) would clash there, so the later type gets a class of its own.
    /// </summary>
    private string ExtensionClassName(MethodSymbol method) =>
        _separateExtensionClasses.TryGetValue((method.DeclaringFile!, method.ContainingType), out var name) ? name : _extensionClassNames[method.DeclaringFile!];

    /// <summary>The entry point, in the global namespace. (Option, Result and the interop helpers are in Piper.Runtime.)</summary>
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
            _writer.WriteLine("public static void Main(string[] args)");
            _writer.OpenBrace();
            // Piper output must not depend on the machine's locale (e.g. "1,5" vs "1.5").
            _writer.WriteLine("global::System.Globalization.CultureInfo.CurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;");
            // An async Main is waited for: `Main().GetAwaiter().GetResult()` (so exceptions keep their own type).
            var call = $"{FunctionContainer(main)}.{Escape(main.Name)}({(main.Parameters.Length == 1 ? "args" : "")})";
            _writer.WriteLine(main.IsAsync ? $"{call}.GetAwaiter().GetResult();" : $"{call};");
            _writer.CloseBrace();
            _writer.CloseBrace();
        }
        return _writer.ToString();
    }

    private static NamespaceSymbol RootOf(NamespaceSymbol @namespace)
    {
        while (@namespace.Parent is not null)
            @namespace = @namespace.Parent;
        return @namespace;
    }

    private string Fields(IEnumerable<FieldSymbol> fields, ISet<string>? shadowedNames = null) =>
        string.Join(", ", fields.Select(f => $"{InlineAttributes(f)}{TypeName(f.Type, shadowedNames)} {Escape(f.Name)}{DefaultValue(f.Default)}"));

    /// <summary>A record's fields as its primary constructor's parameters; when its properties are redeclared (for invariants) their attributes go there.</summary>
    private string RecordFields(RecordSymbol record) =>
        string.Join(", ", record.Fields.Select((f, i) =>
        {
            var attributes = !record.Invariants.IsEmpty
                ? string.Concat(f.Attributes.Where(a => a.Target != "property").Select(a => $"[{Attribute(a)}] "))
                : InlineAttributes(f);
            return $"{attributes}{TypeName(f.Type)} {Escape(f.Name)}{DefaultValue(f.Default)}";
        }));

    /// <summary>
    /// A declaration's `///` comment as C# XML documentation, with a `<param>` for each documented parameter or field, so
    /// C# callers and the generated .xml file see it.
    /// </summary>
    private void EmitDocumentation(Symbol symbol, IEnumerable<Symbol> parameters)
    {
        var documented = parameters.Where(p => p.Documentation is not null).ToList();
        if (symbol.Documentation is null && documented.Count == 0)
            return;
        static string Xml(string text) => System.Security.SecurityElement.Escape(text)!;
        if (symbol.Documentation is { } documentation)
        {
            _writer.WriteLine("/// <summary>");
            foreach (var line in documentation.Split('\n'))
                _writer.WriteLine($"/// {Xml(line)}".TrimEnd());
            _writer.WriteLine("/// </summary>");
        }
        foreach (var parameter in documented)
            _writer.WriteLine($"/// <param name=\"{Xml(parameter.Name)}\">{Xml(parameter.Documentation!.Replace('\n', ' '))}</param>");
    }

    /// <summary>A declaration's attributes, one per line: <c>[global::System.ObsoleteAttribute("Use area")]</c>.</summary>
    private void EmitAttributes(Symbol symbol)
    {
        foreach (var attribute in symbol.Attributes)
            _writer.WriteLine($"[{Attribute(attribute)}]");
    }

    /// <summary>A field's or parameter's attributes, before its type: <c>[property: global::…JsonPropertyNameAttribute("id")] </c>.</summary>
    private string InlineAttributes(Symbol symbol) => string.Concat(symbol.Attributes.Select(a => $"[{Attribute(a)}] "));

    private string Attribute(BoundAttribute attribute)
    {
        var arguments = attribute.Arguments.Select(a => Expr(a, AssignmentPrecedence))
            .Concat(attribute.NamedArguments.Select(n => $"{Escape(n.Name)} = {Expr(n.Value, AssignmentPrecedence)}"))
            .ToList();
        var target = attribute.Target is null ? "" : attribute.Target + ": ";
        return $"{target}{TypeName(attribute.Type)}{(arguments.Count == 0 ? "" : $"({string.Join(", ", arguments)})")}";
    }

    private static string TypeParameterList(IEnumerable<TypeParameterSymbol> parameters) =>
        parameters.Any() ? $"<{string.Join(", ", parameters.Select(p => Escape(p.Name)))}>" : "";

    private string BaseList(IEnumerable<TypeSymbol> traits) =>
        traits.Any() ? $" : {string.Join(", ", traits.Select(t => TypeName(t)))}" : "";

    private string Constraints(IEnumerable<TypeParameterSymbol> parameters) =>
        // Trait bounds are checked by Piper and met through witnesses, since int or string can satisfy one; only .NET
        // interface bounds are C# constraints.
        string.Concat(parameters
            .Where(p => p.Constraints.Any(c => c is ClrTypeSymbol))
            .Select(p => $" where {Escape(p.Name)} : {string.Join(", ", p.Constraints.OfType<ClrTypeSymbol>().Select(c => TypeName(c)))}"));

    /// <summary>
    /// How to name a function from the current position: a local function by the name it has in the method (unique
    /// among its locals), a top-level one through its module class, qualified as needed.
    /// </summary>
    private string FunctionName(FunctionSymbol function) =>
        function switch
        {
            { IsLocal: true } => Escape(_names.Get(function)),
            // A static method of a Piper type, named as a value: `Point.Make`.
            MethodSymbol { ContainingType: var owner } => $"{TypeName(owner)}.{Escape(function.Name)}",
            _ => $"{(InCurrentClass(function) ? "" : FunctionContainer(function) + ".")}{Escape(function.Name)}",
        };

    /// <summary>How to reach a top-level function from the current position: its module class, qualified as needed.</summary>
    private string FunctionContainer(FunctionSymbol function)
    {
        var className = ModuleClassName(function.DeclaringFile!, InFileClass(function));
        return function.Namespace == _namespace ? className : $"global::{Qualified(function.Namespace!, className)}";
    }

    private static string Qualified(NamespaceSymbol @namespace, string name) =>
        @namespace.IsGlobal ? name : $"{@namespace.FullName}.{name}";

    private NameAllocator NewNameAllocator(FunctionSymbol function)
    {
        IEnumerable<string> typeParameters = function.TypeParameters.Select(p => p.Name);
        if (function is MethodSymbol { ContainingType: NamedTypeDefinition definition })
            typeParameters = typeParameters.Concat(definition.TypeParameters.Select(p => p.Name));
        // A class's methods name its constructor parameters bare, so no local may take their names. A parameter of the
        // method may, keeping its name for named arguments: it hides the constructor's throughout, in Piper as in C#.
        if (function is MethodSymbol { ContainingType: ClassSymbol @class })
        {
            typeParameters = typeParameters.Concat(@class.SelfAndBases().SelectMany(c => c.Parameters).Select(p => p.Name)
                .Where(name => !function.Parameters.Any(p => p.Name == name)));
        }
        // In an extension block the receiver parameter is called `self` (C# can't name it `this`), so keep that name free.
        return new NameAllocator([.. _topLevelNames, "_", "self", .. typeParameters]);
    }

    /// <summary>e.g. <c>public static T first&lt;T&gt;(Pair&lt;T, T&gt; pair) where T : Show</c>; declares the parameter names.</summary>
    /// <param name="name">The C# name, if not the function's own (a local function's, made unique among its method's locals).</param>
    private string Signature(FunctionSymbol symbol, string modifiers, string? name = null)
    {
        var parameters = string.Join(", ", symbol.Parameters.Select(p => $"{InlineAttributes(p)}{TypeName(p.Type)} {Escape(_names.Declare(p))}{DefaultValue(p.Default)}"));
        // `async` belongs to a body, so a required trait (interface) method never has it.
        var @async = symbol.IsAsync && symbol.Declaration?.HasBody == true ? "async " : "";
        // `operator +(other: T)` → `public static T operator +(T self, T other)` (C# operators are public and static).
        if (symbol is MethodSymbol { IsOperator: true } @operator)
        {
            var self = @operator.IsStatic ? "" : $"{TypeName(@operator.This!.Type)} self{(symbol.Parameters.IsEmpty ? "" : ", ")}";
            return $"public static {TypeName(symbol.ReturnType)} operator {symbol.Name}({self}{parameters})";
        }
        // A trait's override is an explicit implementation of the parent's method, which inherits its constraints.
        if (symbol is MethodSymbol { Overrides: var (_, parent) })
        {
            return $"{modifiers}{@async}{TypeName(symbol.ReturnType)} {TypeName(parent)}.{Escape(symbol.Name)}{TypeParameterList(symbol.TypeParameters)}" +
                   $"({parameters})";
        }
        return $"{modifiers}{@async}{TypeName(symbol.ReturnType)} {name ?? Escape(symbol.Name)}{TypeParameterList(symbol.TypeParameters)}" +
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
        EmitDocumentation(symbol, symbol.Parameters);
        EmitAttributes(symbol);

        if (function.IsExpressionBodied && ExpressionBody(body) is { } expression)
        {
            _writer.WriteLine($"{signature} => {Expr(expression, AssignmentPrecedence)};");
            return;
        }

        _writer.WriteLine(signature);
        // The directive before the signature numbers the lines after it too, so a first statement on the same Piper line
        // (`fn F() { throw … }`, or a precondition check) needs its own directive, or it would map to a later line.
        _lastLine = -1;
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
        expression is BoundCallExpression or BoundMethodCallExpression or BoundClrInvocationExpression or BoundAssignmentExpression or BoundFieldAssignmentExpression or BoundObjectCreationExpression
            or BoundObjectInitializerExpression or BoundDelegateInvocationExpression or BoundClrAssignmentExpression or BoundDeconstructionExpression or BoundAwaitExpression;

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
                var @using = declaration.IsUsing ? declaration.IsAwait ? "await using " : "using " : "";
                _writer.WriteLine(declaration.Initializer is null
                    ? $"{type} {name};"
                    : $"{@using}{type} {name} = {Expr(declaration.Initializer, AssignmentPrecedence)};");
                break;
            }

            case BoundUsingStatement usingStatement:
            {
                var resource = Expr(usingStatement.Resource, AssignmentPrecedence);
                var awaitUsing = usingStatement.IsAwait ? "await " : "";
                _writer.WriteLine(usingStatement.Variable is { } variable
                    ? $"{awaitUsing}using ({TypeName(variable.Type)} {Escape(_names.Declare(variable))} = {resource})"
                    : $"{awaitUsing}using ({resource})");
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
                var comparison = loop.IsInclusive ? "<=" : "<";
                if (!BoundTreeWalker.IsCapturedIn(loop.Variable, loop.Body))
                {
                    var name = Escape(_names.Declare(loop.Variable));
                    _writer.WriteLine($"for ({type} {name} = {lower}; {name} {comparison} {upper}; {name}++)");
                    EmitBlock(loop.Body);
                    break;
                }

                // A lambda capturing the variable sees that iteration's value, as with foreach, so each iteration
                // gets its own copy rather than sharing C#'s one `for` variable.
                var variable = Escape(_names.Declare(loop.Variable));
                var counter = Escape(_names.Declare(new VariableSymbol(loop.Variable.Name, loop.Variable.Type, isMutable: true, VariableKind.Temporary)));
                _writer.WriteLine($"for ({type} {counter} = {lower}; {counter} {comparison} {upper}; {counter}++)");
                _writer.OpenBrace();
                _writer.WriteLine($"{type} {variable} = {counter};");
                foreach (var inner in loop.Body is BoundBlockStatement block ? block.Statements : [loop.Body])
                    EmitStatement(inner);
                _writer.CloseBrace();
                break;
            }

            case BoundForEachStatement loop:
            {
                var collection = Expr(loop.Collection, AssignmentPrecedence);
                var variable = loop.Deconstruction is { } deconstruction
                    ? $"var {Designation(deconstruction, declares: true)}"
                    : $"{TypeName(loop.Variable.Type)} {Escape(_names.Declare(loop.Variable))}";
                _writer.WriteLine($"{(loop.IsAwait ? "await " : "")}foreach ({variable} in {collection})");
                EmitBlock(loop.Body);
                break;
            }

            case BoundTryCatchStatement attempt:
                EmitTryCatch(attempt);
                break;

            case BoundYieldStatement { Value: { } value }:
                _writer.WriteLine($"yield return {Expr(value, AssignmentPrecedence)};");
                break;

            case BoundYieldStatement:
                _writer.WriteLine("yield break;");
                break;

            case BoundThrowStatement thrown:
                _writer.WriteLine($"throw {Expr(thrown.Exception, AssignmentPrecedence)};");
                break;

            // A C# local function, under the name the method's other code calls it by.
            case BoundLocalFunctionStatement { Function: var function }:
            {
                var signature = Signature(function.Symbol, "", Escape(_names.Get(function.Symbol)));
                var body = (BoundBlockStatement)function.Body;
                if (function.IsExpressionBodied && ExpressionBody(body) is { } expression)
                {
                    _writer.WriteLine($"{signature} => {Expr(expression, AssignmentPrecedence)};");
                }
                else
                {
                    _writer.WriteLine(signature);
                    EmitBlock(body);
                }
                break;
            }

            case BoundUnreachableStatement:
                _writer.WriteLine("throw new global::System.Diagnostics.UnreachableException();");
                break;

            case BoundLabelStatement label:
                _writer.WriteLine($"{LabelName(label.Label)}: ;");
                break;

            case BoundGotoStatement jump:
                _writer.WriteLine($"goto {LabelName(jump.Label)};");
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
        // A builtin or type parameter becomes a trait value through its witness, which boxes it if needed.
        if (expression is BoundConversionExpression conversion)
        {
            return conversion.Type.IsTrait && UsesWitness(conversion.Expression.Type)
                ? $"{WitnessOf(conversion.Type, conversion.Expression.Type)}.PiperBox({Expr(conversion.Expression, ConditionalPrecedence)})"
                : Expr(conversion.Expression, minPrecedence);
        }

        var (text, precedence) = expression switch
        {
            BoundLiteralExpression literal => (Literal(literal.Value), PrimaryPrecedence),
            BoundVariableExpression { Variable.Kind: VariableKind.This } => (_thisName, PrimaryPrecedence),
            BoundVariableExpression { Variable.Kind: VariableKind.Base } => ("base", PrimaryPrecedence),
            BoundVariableExpression variable => (VariableName(variable.Variable), PrimaryPrecedence),
            BoundObjectCreationExpression creation =>
                ($"new {TypeName(creation.Type)}({Arguments(creation.Arguments, creation.ArgumentNames)})", PrimaryPrecedence),
            BoundObjectInitializerExpression initializer => (
                initializer.Members.IsEmpty
                    ? Expr(initializer.Creation, PrimaryPrecedence)
                    : $"{Expr(initializer.Creation, PrimaryPrecedence)} {InitializerMembers(initializer.Members)}",
                PrimaryPrecedence),
            // Only a member's value in an initializer: `Font = { Size = 12 }`, `Items = { "a", { "k", 1 } }`.
            BoundNestedInitializer nested => (InitializerMembers(nested.Members), PrimaryPrecedence),
            BoundCollectionInitializer collection => (
                $"{{ {string.Join(", ", collection.Elements.Select(e => e.Length == 1 ? Expr(e[0], AssignmentPrecedence) : $"{{ {string.Join(", ", e.Select(a => Expr(a, AssignmentPrecedence)))} }}"))} }}",
                PrimaryPrecedence),
            BoundFieldAssignmentExpression assignment =>
                ($"{Expr(assignment.Target, PrimaryPrecedence)}.{Escape(assignment.Field.Name)} {(assignment.CompoundOperator is { } op ? Operators.GetText(op) + "=" : "=")} {Expr(assignment.Value, AssignmentPrecedence)}", AssignmentPrecedence),
            BoundUnaryExpression unary => (Unary(unary), UnaryPrecedence),
            BoundBinaryExpression binary => Binary(binary),
            BoundAssignmentExpression assignment => (Assignment(assignment), AssignmentPrecedence),
            BoundCallExpression call => (Call(call), PrimaryPrecedence),
            BoundMethodCallExpression call => (MethodCall(call), SetterOf(call.Method) is not null && !UsesWitnessFor(call) ? AssignmentPrecedence : PrimaryPrecedence),
            BoundRecordCreationExpression creation =>
                ($"new {TypeName(creation.Type)}({Arguments(creation.Arguments, creation.ArgumentNames)})", PrimaryPrecedence),
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
            BoundRefArgument reference => ($"ref {Expr(reference.Target, PrimaryPrecedence)}", PrimaryPrecedence),
            BoundClrAssignmentExpression assignment => (ClrAssignment(assignment), AssignmentPrecedence),
            BoundIndexExpression index =>
                ($"{Expr(index.Target, PrimaryPrecedence)}[{Arguments(index.Arguments)}]", PrimaryPrecedence),
            BoundCollectionExpression { HasSpread: true } collection => (SpreadCollection(collection), UnaryPrecedence),
            BoundCollectionExpression collection => (Collection(collection), PrimaryPrecedence),
            // The operand is parenthesized unless primary: C# reads `(Point)-x` as a subtraction.
            // C#'s `with` binds tighter than `*` but looser than unary operators.
            BoundRecordSpreadExpression spread => (
                spread.Updates.IsEmpty
                    ? $"{Expr(spread.Source, PrimaryPrecedence)} with {{ }}"
                    : $"{Expr(spread.Source, PrimaryPrecedence)} with {{ {string.Join(", ", spread.Updates.Select(u => $"{Escape(u.Field.Name)} = {Expr(u.Value, AssignmentPrecedence)}"))} }}",
                MultiplicativePrecedence),
            BoundAwaitExpression await => ($"await {Expr(await.Operand, UnaryPrecedence)}", UnaryPrecedence),
            // A trait value holding a builtin holds it boxed.
            BoundCastExpression cast when cast.Operand.Type.IsTrait && UsesWitness(cast.Type) =>
                ($"{TraitsClassName}.Unbox<{TypeName(cast.Type)}>({Expr(cast.Operand, ConditionalPrecedence)})", PrimaryPrecedence),
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
                ($"{FunctionName(reference.Function)}{TypeArgumentList(reference.TypeArguments)}", PrimaryPrecedence),
            BoundDelegateInvocationExpression invocation => (DelegateInvocation(invocation), PrimaryPrecedence),
            // Lowering leaves throw expressions only where C# allows them (a ?: branch, the right of ??), which can't be parenthesized.
            BoundThrowExpression thrown => ($"throw {Expr(thrown.Exception, LogicalOrPrecedence)}", PrimaryPrecedence),
            BoundUnreachableExpression => ("default!", PrimaryPrecedence),
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
        type is TypeParameterSymbol || type.IsTrait || (type is TupleTypeSymbol tuple && tuple.ElementTypes.Any(NeedsEqualityComparer));

    private string Assignment(BoundAssignmentExpression assignment)
    {
        var op = assignment.CompoundOperator is { } compound ? Operators.GetText(compound) + "=" : "=";
        return $"{VariableName(assignment.Variable)} {op} {Expr(assignment.Value, AssignmentPrecedence)}";
    }

    /// <summary>
    /// A variable's C# name: a class field named bare is <c>this.name</c> (so no local can hide it), a class's
    /// constructor parameter keeps its own name (which locals can't take), and others get unique ones.
    /// </summary>
    private string VariableName(VariableSymbol variable) => variable.Kind switch
    {
        VariableKind.Field => $"{_thisName}.{Escape(variable.Name)}",
        VariableKind.StaticField => $"{TypeName(variable.Container!)}.{Escape(variable.Name)}",
        VariableKind.ClassParameter => Escape(variable.Name),
        _ => Escape(_names.Get(variable)),
    };

    private string LabelName(BoundLabel label)
    {
        if (!_labels.TryGetValue(label, out var name))
            _labels[label] = name = $"{label.Name}_{_labels.Count + 1}";
        return name;
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

    /// <summary><c>(List&lt;int&gt;)[1, .. xs]</c>: a collection initializer can't spread, so use a collection expression and cast it to give it a type.</summary>
    private string SpreadCollection(BoundCollectionExpression collection)
    {
        var elements = collection.Elements.Select(e =>
            e is BoundSpreadElement spread ? $".. {Expr(spread.Source, AssignmentPrecedence)}" : Expr(e, AssignmentPrecedence));
        return $"({TypeName(collection.Type)})[{string.Join(", ", elements)}]";
    }

    /// <summary>
    /// <c>(int x) =&gt; x + 1</c>. Parameter types are always written, which gives the lambda a natural type in C#
    /// too. A block body is written with a nested writer at the current indentation so it lines up with the code around it.
    /// </summary>
    private string Lambda(BoundLambdaExpression lambda)
    {
        // Its parameters keep their names where they can (so `(id: int) => …` binds a route's {id}); not where they'd hide
        // something the lambda uses from outside it.
        var taken = BoundTreeWalker.DescendantsAndSelf(lambda.Body)
            .Select(node => node switch
            {
                BoundVariableExpression read => read.Variable,
                BoundAssignmentExpression written => written.Variable,
                _ => null,
            })
            .OfType<VariableSymbol>()
            .Select(_names.NameOf)
            .OfType<string>()
            .ToHashSet();
        // A lambda whose Options may be left out takes them nullable, `Option<string>? name`, so ASP.NET Core binds a
        // request lacking one (no ?name=) rather than refusing it; the body reads it as an Option under another name.
        var parameters = new List<string>();
        var unwrapped = new List<string>();
        foreach (var parameter in lambda.Parameters)
        {
            var name = Escape(_names.DeclareLambdaParameter(parameter, taken));
            if (lambda.OptionsMayBeLeftOut && parameter.Type.IsOption)
            {
                var type = TypeName(parameter.Type);
                parameters.Add($"{type}? {name}");
                unwrapped.Add($"{type} {Escape(_names.Declare(parameter))} = {name} ?? default;");
            }
            else
                parameters.Add($"{TypeName(parameter.Type)} {name}");
        }
        var head = $"{(lambda.IsAsync ? "async " : "")}({string.Join(", ", parameters)}) =>";
        var body = (BoundBlockStatement)lambda.Body;
        if (unwrapped.Count == 0 && lambda.IsExpressionBodied && ExpressionBody(body) is { } expression)
            return $"{head} {Expr(expression, AssignmentPrecedence)}";

        var outer = _writer;
        _writer = new CodeWriter(outer.Indent);
        try
        {
            if (unwrapped.Count == 0)
            {
                EmitBlock(body);
                return $"{head} {_writer.ToString().Trim()}";
            }
            _writer.OpenBrace();
            foreach (var line in unwrapped)
                _writer.WriteLine(line);
            EmitBlock(body);
            _writer.CloseBrace();
            return $"{head} {_writer.ToString().Trim()}";
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
        branch is BoundConversionExpression { Type.IsTrait: true, Type: var trait } conversion
            ? $"({TypeName(trait)}){Expr(UsesWitness(conversion.Expression.Type) ? conversion : conversion.Expression, UnaryPrecedence)}"
            : Expr(branch, ConditionalPrecedence);

    /// <summary>
    /// <c>receiver.Method&lt;T&gt;(args)</c>. An out argument whose variable is an Option (.NET may write null) goes to a
    /// temporary that a helper converts once the call returns:
    /// <c>PiperInterop.AfterOutReference(d.TryGetValue(k, out var t), t, out value)</c>.
    /// </summary>
    private string ClrInvocation(BoundClrInvocationExpression invocation)
    {
        var (call, conversions) = ClrCallWithOutTemps(invocation);
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
        var names = invocation.ArgumentNames;
        var arguments = string.Join(", ", invocation.Arguments.Select((argument, i) =>
        {
            var name = !names.IsDefaultOrEmpty && names[i] is { } parameter ? $"{Escape(parameter)}: " : "";
            if (argument is not BoundOutArgument { Boundary: not ClrBoundary.None, Variable: not null } output)
                return name + Arguments([argument]);
            var temp = Escape(_names.Declare(new VariableSymbol("outValue", output.ClrType, isMutable: true, VariableKind.Temporary)));
            conversions.Add((temp, output));
            return $"{name}out var {temp}";
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
        // `Console.WriteLine(xs?.Count)`: an int? passed as object? is boxed, as ToNullableReference needs a class.
        var helper = boundary.Kind == NullableBoundaryKind.ToNullableReference && !ClrTypes.IsReferenceType(boundary.Operand.Type.TypeArguments[0])
            ? "ToNullableObject"
            : boundary.Kind.ToString();
        return $"global::{InteropClassName}.{helper}({Expr(boundary.Operand, AssignmentPrecedence)})";
    }

    private string Call(BoundCallExpression call)
    {
        if (call.Function == BuiltinFunctions.Print)
            return $"global::System.Console.WriteLine({Expr(call.Arguments[0], ConditionalPrecedence)})";
        if (call.Function == BuiltinFunctions.Assert)
            return $"global::Piper.Runtime.PiperAssert.True({Arguments(call.Arguments)})";
        if (call.Function == BuiltinFunctions.Require)
            return $"global::Piper.Runtime.PiperContract.Require({Arguments(call.Arguments)})";
        if (call.Function == BuiltinFunctions.AssertEqual)
            return $"global::Piper.Runtime.PiperAssert.Equal{TypeArgumentList(call.TypeArguments)}({Arguments(call.Arguments)})";
        return $"{FunctionName(call.Function)}{TypeArgumentList(call.TypeArguments)}({Arguments(call.Arguments, call.ArgumentNames)})";
    }

    /// <summary>
    /// <c>receiver.method(args)</c> or <c>Type.method(args)</c>. C# only exposes a default interface method
    /// through the interface, so a default trait method called on a concrete type casts the receiver first.
    /// </summary>
    /// <summary>
    /// For a property's getter (<c>get_Area</c>, from a trait, an extend block or a class): the property, which C# code
    /// reads by name. Null for other methods, and for indexers' accessors.
    /// </summary>
    private static string? PropertyOf(MethodSymbol method) =>
        method.IsAccessor && !method.IsIndexerAccessor && method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Declaration!.AccessorOf : null;

    /// <summary>For a property's setter (<c>set_Name</c>): the property, which C# code assigns by name. Null for other methods.</summary>
    private static string? SetterOf(MethodSymbol method) =>
        method.IsAccessor && !method.IsIndexerAccessor && method.Name.StartsWith("set_", StringComparison.Ordinal) ? method.Declaration!.AccessorOf : null;

    private string MethodCall(BoundMethodCallExpression call)
    {
        var typeArguments = TypeArgumentList(call.TypeArguments);
        var arguments = Arguments(call.Arguments, call.ArgumentNames);

        // A type parameter, builtin, .NET or runtime type reaches a trait's methods through its witness:
        // `PiperTraits.Witness<Shape.PiperWitness<T>>().area(x)`. So does a static default on any type.
        if (UsesWitnessFor(call) && call.TraitType is { } trait)
        {
            var receiver = call.Receiver is null ? "" : Expr(call.Receiver, ConditionalPrecedence);
            var all = receiver == "" ? arguments : arguments == "" ? receiver : $"{receiver}, {arguments}";
            return $"{WitnessOf(trait, call.ReceiverType)}.{Escape(call.Method.Name)}{typeArguments}({all})";
        }

        // Extend methods of builtin, .NET and runtime types are C# extension members.
        if (call.Method.ContainingType is var owner && !owner.IsTrait && owner is not NamedTypeDefinition { IsRuntimeType: false })
        {
            var receiver = call.Receiver is null ? null : Expr(call.Receiver, PrimaryPrecedence);
            return ExtensionCall(call.Method, call.ReceiverType, receiver, typeArguments, arguments);
        }

        // A property's getter is read as the property, and its setter assigns it.
        var method = PropertyOf(call.Method) is { } property ? Escape(property)
            : SetterOf(call.Method) is { } settable ? $"{Escape(settable)} = {Expr(call.Arguments[0], AssignmentPrecedence)}"
            : $"{Escape(call.Method.Name)}{typeArguments}({arguments})";
        if (call.Receiver is null)
            return $"{TypeName(call.ReceiverType)}.{method}";

        if (call.TraitType is { } implemented && !call.ReceiverType.IsTrait)
            return $"(({TypeName(implemented)}){Expr(call.Receiver, UnaryPrecedence)}).{method}";

        return $"{Expr(call.Receiver, PrimaryPrecedence)}.{method}";
    }

    /// <summary>Whether <paramref name="call"/> reaches its trait method through the trait's witness.</summary>
    private bool UsesWitnessFor(BoundMethodCallExpression call) =>
        call.TraitType is not null && !call.ReceiverType.IsTrait && (UsesWitness(call.ReceiverType) || call.Receiver is null);

    private string TypeArgumentList(ImmutableArray<TypeSymbol> arguments) =>
        arguments.IsEmpty ? "" : $"<{string.Join(", ", arguments.Select(a => TypeName(a)))}>";

    /// <summary><c>{ Title = "x", Font = { Size = 12 } }</c>: an object initializer's members.</summary>
    private string InitializerMembers(ImmutableArray<(string Member, BoundExpression Value)> members) =>
        $"{{ {string.Join(", ", members.Select(m => $"{Escape(m.Member)} = {Expr(m.Value, AssignmentPrecedence)}"))} }}";

    /// <param name="names">Named arguments' names, by position (empty when all are positional), written as C#'s `name: value`.</param>
    private string Arguments(IEnumerable<BoundExpression> arguments, ImmutableArray<string?> names = default) =>
        string.Join(", ", arguments.Select((argument, i) =>
        {
            // A lambda needs no parentheses in an argument list.
            var text = Expr(argument, Unwrap(argument) is BoundLambdaExpression ? AssignmentPrecedence : ConditionalPrecedence);
            // `f(a < b, c > d)` could be misread by C# as a generic instantiation; parenthesize to be safe.
            if (Unwrap(argument) is BoundBinaryExpression { Operator: BoundBinaryOperatorKind.Less or BoundBinaryOperatorKind.Greater })
                text = $"({text})";
            return !names.IsDefaultOrEmpty && names[i] is { } name ? $"{Escape(name)}: {text}" : text;
        }));

    /// <summary>` = 443` after a parameter with a default value (` = default` for an optional's None), or nothing.</summary>
    private string DefaultValue(ParameterDefault? @default) => @default switch
    {
        null => "",
        { Value: BoundVariantCreationExpression } => " = default",
        { Value: var value } => $" = {Expr(value, AssignmentPrecedence)}",
    };

    private string InterpolatedString(BoundInterpolatedStringExpression interpolated)
    {
        if (interpolated.Parts.All(p => p is BoundLiteralExpression { Value: string }))
            return Literal(string.Concat(interpolated.Parts.Select(p => (string)((BoundLiteralExpression)p).Value)));

        var builder = new StringBuilder("$\"");
        foreach (var part in interpolated.Parts)
        {
            if (part is BoundLiteralExpression { Value: string text })
                builder.Append(EscapeString(text, interpolated: true));
            else if (part is BoundFormattedExpression formatted)
            {
                var hole = Expr(formatted.Value, LogicalOrPrecedence);
                builder.Append('{').Append(hole.Contains("::") && !IsParenthesized(hole) ? $"({hole})" : hole);
                if (formatted.Alignment is { } alignment)
                    builder.Append(',').Append(alignment);
                if (formatted.Format is { } format)
                    builder.Append(':').Append(EscapeString(format, interpolated: false));
                builder.Append('}');
            }
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
    /// patterns when fieldless), ranges become relational patterns, '|' becomes 'or', and list, property and relational
    /// patterns are C#'s own (`...rest` becoming `.. var rest`).
    /// </summary>
    private string Pattern(BoundPattern pattern) => pattern switch
    {
        BoundWildcardPattern => "_",
        BoundBindingPattern binding => $"var {Escape(_names.Declare(binding.Variable))}",
        BoundTypePattern { Variable: { } variable } typed => $"{TypeName(typed.TestedType)} {Escape(_names.Declare(variable))}",
        BoundTypePattern typed => TypeName(typed.TestedType),
        BoundLiteralPattern literal => Literal(literal.Value),
        BoundRangePattern range =>
            $">= {Literal(range.Lower)} and {(range.IsInclusive ? "<=" : "<")} {Literal(range.Upper)}",
        BoundConstructorPattern { Constructor: VariantSymbol { Enum.BuiltinKind: not BuiltinEnumKind.None } } builtin => BuiltinPattern(builtin),
        BoundConstructorPattern { Arguments.Length: 0 } constructor => ConstructorName(constructor.Constructor, constructor.Type),
        BoundConstructorPattern constructor =>
            $"{ConstructorName(constructor.Constructor, constructor.Type)}({string.Join(", ", constructor.Arguments.Select(Pattern))})",
        BoundTuplePattern tuple => $"({string.Join(", ", tuple.Elements.Select(Pattern))})",
        BoundListPattern list => ListPattern(list),
        BoundPropertyPattern properties =>
            $"{{ {string.Join(", ", properties.Properties.Select(p => $"{Escape(p.Member.Name)}: {Pattern(p.Pattern)}"))} }}",
        BoundRelationalPattern relational => $"{relational.Operator} {Literal(relational.Value)}",
        BoundOrPattern alternatives => string.Join(" or ", alternatives.Alternatives.Select(a =>
            a is BoundRangePattern ? $"({Pattern(a)})" : Pattern(a))),
        _ => throw new InvalidOperationException($"Unexpected pattern {pattern.GetType().Name}"),
    };

    private string ListPattern(BoundListPattern list)
    {
        var elements = list.Elements.Select(Pattern).ToList();
        if (list.Slice is { } slice)
            elements.Insert(list.SliceIndex, slice is BoundWildcardPattern ? ".." : $".. {Pattern(slice)}");
        return $"[{string.Join(", ", elements)}]";
    }

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

        // Types from another namespace (or hidden by a variant of the same name) are written in full.
        string Qualify(Symbol definition)
        {
            var name = Escape(definition.Name);
            // Option and Result come from Piper.Runtime, imported by every generated file.
            if (definition is NamedTypeDefinition { IsRuntimeType: true })
                return shadowedNames?.Contains(definition.Name) == true ? $"global::Piper.Runtime.{name}" : name;
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
        var root = type.Assembly.GetName().Name is { } assembly && _aliases.TryGetValue(assembly, out var alias) ? alias : "global";
        return $"{root}::{type.Namespace}.{type.Name.Split('`')[0]}";
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

    /// <summary>Gives each variable (and local function) in a method a unique C# name that doesn't collide with top-level names.</summary>
    private sealed class NameAllocator(IEnumerable<string> reserved)
    {
        private readonly HashSet<string> _reserved = [.. reserved];
        private readonly HashSet<string> _used = [.. reserved];
        private readonly Dictionary<Symbol, string> _names = [];

        public string Declare(Symbol variable)
        {
            var name = variable.Name;
            for (int n = 1; !_used.Add(name); n++)
                name = $"{variable.Name}_{n}";
            _names[variable] = name;
            return name;
        }

        /// <summary>
        /// A lambda's parameter, under its own name if it can have it: C# lets a lambda's parameters (unlike a nested
        /// block's locals) hide the enclosing method's names, and something may read the name, as ASP.NET Core binds a
        /// route's `{id}` to the handler's `id`. Not a reserved name, nor one of <paramref name="taken"/>, the C# names of
        /// what the lambda uses from outside it, which it would hide.
        /// </summary>
        public string DeclareLambdaParameter(Symbol variable, IReadOnlySet<string> taken)
        {
            if (_reserved.Contains(variable.Name) || taken.Contains(variable.Name))
                return Declare(variable);
            _used.Add(variable.Name);
            _names[variable] = variable.Name;
            return variable.Name;
        }

        /// <summary>The C# name given to <paramref name="variable"/>, if it's been declared.</summary>
        public string? NameOf(Symbol variable) => _names.GetValueOrDefault(variable);

        public string Get(Symbol variable) => _names.TryGetValue(variable, out var name) ? name : Declare(variable);
    }
}
