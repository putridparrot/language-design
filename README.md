# Introduction

This repository is used for programming language design ideas, tests and implementations. I'm not sure how far I'll
take the languages, but the initial idea is to reuse the .NET runtime, so a language can pull in packages and existing
runtime functionality to jump-start what you can do with it.

## Aims

I wanted to play with writing one or more new programming languages, in some cases including transpilers, compilers
and a language server, so I can use Visual Studio Code as the IDE for each language.

## Piper

I started by asking Claude to implement a modern language taking the best features of C#, Java, Rust, Swift, Kotlin,
F# and TypeScript. Piper isn't actually the first language; that's Parrot. I wanted to restart the process because
Parrot had more features than I wanted for an experimentation language. In truth Parrot and Piper are very similar.

_Note: I'll likely add Parrot to the repository soon._

I was interested to see what an AI would come back with. Pure OO was a lower priority than I expected (as an OO
developer of many years), so the design leans towards a more function based style, like Rust's (not a functional-first language like F#). The best was to describe the languages is, as a "data and traits first, expression-oriented, with classes for .NET".

Piper is a full-featured language, but it's aimed at the core features. With Parrot I was looking to build
concurrency and distributed features into the language. With Piper I'm more interested in a smaller language, and in
ways to extend it without changing the compiler: see [piper/docs/plugins.md](piper/docs/plugins.md).

- [piper/README.md](piper/README.md): installing, building and the repository layout.
- [piper/docs/tour.md](piper/docs/tour.md): a tour of the language for C# developers.
- [piper/docs/language.md](piper/docs/language.md): the reference, including the grammar and how each construct
  maps to C#.

---

# A guide to language design and implementation

The rest of this page explains the ideas and vocabulary behind designing and implementing a programming language. It
follows the path a program takes, from source text to running code. Where it helps, it points at the part of Piper
that does each job, so you can read real code alongside the definitions.

## The big picture: from text to a running program

A language implementation is a pipeline. Each stage turns one representation of the program into another, closer to
something a machine can run:

```text
source text      "let x = 1 + 2;"
   │  lexer (scanner, tokenizer)
   ▼
tokens           [let] [x] [=] [1] [+] [2] [;]
   │  parser
   ▼
syntax tree      VariableDeclaration(x, Binary(1, +, 2))
   │  binder / type checker (semantic analysis)
   ▼
bound tree       x: int = Binary(int 1, +, int 2) : int
   │  lowering, optimization
   ▼
IR / simpler tree
   │  code generation (back end)
   ▼
target           C# source, .NET IL, JVM bytecode, LLVM IR or machine code
```

**Front end and back end.**

- **The front end** understands the language: lexing, parsing and semantic analysis. It reports the user's errors.
- **The back end** produces the target: code generation and, usually, optimization.
- **The middle end** is a name sometimes given to optimization passes that are independent of both the language and
  the target.

Separating the front end from the back end is what lets one back end serve many languages. LLVM serves Rust, Swift,
C and C++ that way, and the .NET runtime serves C#, F#, VB and Piper.

**Piper's pipeline:**

- **Stages:** `Lexer` → `Parser` → `Binder` → `Lowerer` → `CSharpEmitter`, run by
  [Compilation.cs](piper/src/Piper.Compiler/Compilation.cs).
- **Running and building:** Roslyn (the C# compiler, used as a library) turns the generated C# into a .NET assembly.
  [Runner.cs](piper/src/Piper.Compiler/Runner.cs) does this in memory to run a program, and
  [AssemblyWriter.cs](piper/src/Piper.Compiler/AssemblyWriter.cs) writes it to disk.

## Ways to run a program

| Approach | What it does | Examples |
|---|---|---|
| **Interpreter (tree-walking)** | Walks the syntax tree and evaluates each node directly. The simplest to write; slow. | Early Ruby, many teaching languages |
| **Bytecode interpreter / virtual machine** | Compiles to a compact instruction set (bytecode) for a virtual machine, which loops over the instructions. | CPython, Lua |
| **Ahead-of-time (AOT) compiler** | Compiles to machine code before the program runs. Fast start-up, no compiler at run time. | C, Rust, Go, .NET Native AOT |
| **Just-in-time (JIT) compiler** | Compiles bytecode to machine code while the program runs, and can optimize using what it sees at run time. | The .NET runtime (RyuJIT), the JVM (HotSpot), JavaScript engines (V8) |
| **Transpiler (source-to-source compiler)** | Compiles to another high-level language, whose own compiler does the rest. | TypeScript → JavaScript, Piper → C# |

- **Bytecode:** instructions for a virtual machine rather than a real processor, so the same file runs anywhere the VM
  runs. .NET's is CIL (Common Intermediate Language, also called IL or MSIL); Java's is JVM bytecode.
- **Runtime:** everything a program needs while it runs besides its own code: the garbage collector, the type system's
  metadata, exception handling, threads and the standard library. Targeting an existing runtime (.NET, the JVM) gives a
  new language all of that, and its libraries, for free. That's the main reason this repository targets .NET.
- **IR (intermediate representation):** any form of the program between the source and the target, designed to be easy
  to analyze and transform. A syntax tree is a high-level IR. Lower-level IRs look more like idealized machine code.
- **SSA (static single assignment):** a common low-level IR form where every variable is assigned exactly once. It makes
  many optimizations simpler. LLVM IR is in SSA form.
- **LLVM:** a reusable compiler back end. A front end produces LLVM IR, and LLVM optimizes it and generates machine code
  for many processors. Rust, Swift, Clang (C/C++) and Zig use it.
- **WebAssembly (Wasm):** a portable bytecode that browsers (and other hosts) run, another possible target.

Piper is a transpiler, the quickest way to a complete language on .NET:

- **What it gets for free:** C#'s compiler handles code generation and IL, and Piper gets C#'s performance and
  debugging along with all of .NET.
- **The cost:** Piper can only express what C# can.
- **Alternatives:** emitting IL directly (with `System.Reflection.Emit` or a library such as Mono.Cecil) removes that
  limit, at the cost of writing a back end.

## The front end

### Lexical analysis (lexing, scanning, tokenizing)

The **lexer** turns a stream of characters into a stream of **tokens**: the words of the language.

- **Token:** a classified piece of source text: a keyword (`let`), an identifier (`total`), a literal (`42`,
  `"hi"`), an operator (`+=`) or punctuation (`{`). A token usually records its kind, its text and its position
  (span), so errors can point at it.
- **Lexeme:** the actual text a token was made from.
- **Trivia:** whitespace and comments, which the parser doesn't need but tools (formatters, documentation comments)
  might. Roslyn keeps trivia attached to tokens so it can reproduce the source exactly.
- **Keyword:** a word the language reserves for its own syntax.
  - **Reserved words** can't be used as names, even when they're not yet keywords. Reserving words a language might
    use later lets it grow without breaking programs.
  - **Contextual keywords** are keywords only in certain positions. In C# and Piper, `yield` and `async` are
    contextual, so they can still be ordinary names elsewhere.
- **Literal:** a value written directly in source: numbers, strings, characters, `true`.
- **String interpolation:** holes in a string literal (`"total: {total}"`) that the lexer marks and the parser parses
  as expressions.

In Piper: [Lexer.cs](piper/src/Piper.Compiler/Syntax/Lexer.cs), with keywords in
[SyntaxFacts.cs](piper/src/Piper.Compiler/Syntax/SyntaxFacts.cs).

### Syntax and grammars

- **Syntax:** the form of programs, meaning which sequences of tokens are valid.
- **Semantics:** what valid programs mean.
- **Grammar:** the rules defining the syntax. Most programming languages are described by a **context-free grammar**,
  a set of rules like `while = "while" expression block ;`.
- **BNF / EBNF:** notations for writing grammars. EBNF (Extended Backus–Naur Form) adds `{ x }` for repetition and
  `[ x ]` for optional parts. Piper's grammar is in EBNF in [language.md](piper/docs/language.md).
- **Ambiguity:** when one piece of text could be parsed two ways. A language must pick one, by a rule or by changing
  the syntax.
  - **The dangling else:** in `if a if b x else y`, which `if` does the `else` belong to?
  - **Trailing closures:** Piper has one ambiguity of its own. In `if ready { … }`, is `ready { … }` a call with a
    trailing closure, or is `{ … }` the `if`'s body? Piper's rule, like Swift's, is that in an `if`, `while`, `for`
    or `match` head the brace is the body.
- **Syntactic sugar:** syntax that's a nicer way to write something the language can already express. Removing it is
  called **desugaring**, and is often done early so later stages see fewer constructs. Examples: Piper's trailing
  closures (`f(a) { x => … }` is `f(a, x => { … })`) and C#'s `foreach`, which is a loop over an enumerator.

### Parsing

The **parser** turns tokens into a tree that shows the program's structure.

- **Parse tree (concrete syntax tree):** follows the grammar exactly, including every token, parenthesis and
  semicolon.
- **AST (abstract syntax tree):** keeps only what matters for meaning. `(1 + 2)` and `1 + 2` give the same AST
  node. Most compilers build an AST directly; Roslyn builds a full-fidelity concrete tree, because its trees also
  serve editors and refactoring tools. (The two terms are often used loosely, but they're not the same thing.)
- **Recursive descent:** a hand-written parser with one function per grammar rule, each calling the others. It's
  simple, fast, easy to give good error messages and the usual choice for production compilers: C#, Go, Rust and
  Piper all use one.
- **Precedence and associativity:** rules saying how operators group.
  - Precedence: `1 + 2 * 3` is `1 + (2 * 3)`, because `*` binds tighter.
  - Associativity: `a - b - c` is `(a - b) - c` (left-associative), while `a = b = c` is `a = (b = c)`
    (right-associative).
- **Pratt parsing / precedence climbing:** a neat way to parse expressions with many operators, driven by a table of
  binding powers instead of one grammar rule per precedence level.
- **Lookahead:** how many tokens the parser peeks at before deciding what it's looking at.
- **Parser generators:** tools that build a parser from a grammar file, such as yacc/bison, ANTLR and tree-sitter.
  They're less common for compilers than for quick prototypes and editor tooling.
- **Error recovery:** carrying on after a syntax error so the user sees all their mistakes, not just the first. The
  parser reports the problem, invents ("synthesizes") the missing token or an error node, and resynchronizes at a
  safe point such as the next `;` or `}`. A language server depends on this, since code being typed is nearly always
  incomplete.

In Piper: [Parser.cs](piper/src/Piper.Compiler/Syntax/Parser.cs) (recursive descent, with Pratt-style expression
parsing) and the AST in [Ast.cs](piper/src/Piper.Compiler/Syntax/Ast.cs). Each `Parse…` method's comment shows the
grammar rule it implements.

## Semantic analysis: what the program means

Parsing proves a program is well-formed; semantic analysis works out what it means and whether it makes sense.

- **Binding (name resolution):** connecting each use of a name to the declaration it refers to. In `total += n`,
  binding finds which `total` and which `n`.
- **Symbol:** the compiler's record of a declared thing (a variable, function, type, parameter or namespace) with its
  name, type and other facts.
- **Symbol table:** the structure that maps names to symbols, usually one table per scope chained to the enclosing
  one.
- **Scope:** the region of the program where a name is visible.
  - **Lexical (static) scope:** what a name means is decided by where it's written. Almost all modern languages work
    this way.
  - **Dynamic scope:** what a name means depends on the call stack at run time. Early Lisps and Bash work this way.
- **Shadowing:** declaring a name that hides one from an enclosing scope. Rust and Piper allow `let x = x + 1;`;
  C# forbids shadowing a local.
- **Type checking:** verifying that operations are applied to values of suitable types (you can't add a string to a
  record) and reporting those that aren't.
- **Type inference:** working out types the programmer didn't write.
  - **Local inference:** `let x = 1 + 2;` gives `x` the type `int`, and a lambda takes its parameter types from where
    it's used. C#, Kotlin, Swift and Piper infer this way.
  - **Global inference (Hindley–Milner):** can infer the types of whole functions from how they're used. ML,
    OCaml, Haskell and F# do this.
- **Bound tree / annotated AST:** the tree after binding, with every name resolved to a symbol and every expression's
  type known. Later stages work on this, not on raw syntax. Piper's is in
  [BoundTree.cs](piper/src/Piper.Compiler/Binding/BoundTree.cs).
- **Semantic model:** what the binder records so tools can ask questions such as "what does this name refer to?" or
  "what type is this expression?". Hover, go-to-definition and rename are built on it. See
  [SemanticModel.cs](piper/src/Piper.Compiler/Binding/SemanticModel.cs).
- **Diagnostics:** errors and warnings, each with a code (Piper's are `PIP0001`…), a message and a source span.
  Good diagnostics say what's wrong, where, and ideally how to fix it. Much of a language's usability lives here.
- **Definite assignment and flow analysis:** checking, by following every path through the code, that a variable is
  assigned before it's read, that a function returns on every path, or that code is unreachable.

## Type systems

- **Static and dynamic typing:**
  - **Static typing:** types are checked before the program runs (C#, Rust, Piper).
  - **Dynamic typing:** types are checked as it runs (Python, JavaScript).
  - **Gradual typing:** mixes the two (TypeScript, Python's type hints).
- **Strong and weak typing:** loosely, how much a language silently converts between types (`"1" + 1`). These terms are
  used inconsistently; prefer to say exactly which conversions are implicit.
- **Nominal and structural typing:**
  - **Nominal:** two types are compatible because they're declared to be (`class Dog : Animal`). C#, Java and Piper
    are nominal.
  - **Structural:** two types are compatible because they have the same shape. TypeScript and Go's interfaces are
    structural.
- **Product type:** a value holding several values at once, such as a record, struct or tuple.
- **Sum type (tagged union, discriminated union):** a value that is one of several alternatives, each possibly
  carrying data: `enum Shape { Circle(r: double), Rect(w: double, h: double) }`. Rust's and Piper's `enum`, and F#'s
  unions, are sum types.
- **Algebraic data types:** product and sum types together.
- **Pattern matching:** taking a value apart by its shape: `match shape { Circle(r) => …, Rect(w, h) => … }`.
  **Exhaustiveness checking** means the compiler reports a `match` that misses a case. Piper's is in
  [Exhaustiveness.cs](piper/src/Piper.Compiler/Binding/Exhaustiveness.cs).
- **Null safety:** making "no value" part of the type, so it can't be forgotten. Piper's `T?` is `Option<T>`, which
  must be checked before use. This is Tony Hoare's "billion-dollar mistake" fixed by the type system, as in Rust,
  Swift and Kotlin.
- **Errors as values:** returning `Result<T, E>` rather than throwing, so the type says a function can fail and the
  caller must deal with it. Rust is the best-known example; Piper's `?` operator passes an error up, like Rust's.
- **Generics (parametric polymorphism):** code written once for any type: `List<T>`, `fn First<T>(items: List<T>) => T`.
  Languages implement them in different ways:
  - **Monomorphization:** generate a copy for each type used. Rust and C++ templates do this; it's fast code but bigger
    binaries.
  - **Type erasure:** compile one copy and forget the type argument at run time. Java does this.
  - **Reification:** keep type arguments at run time. .NET does this, so `List<int>` and `List<string>` are distinct
    types the runtime knows about.
- **Bounds / constraints:** limits on what a type argument may be: `T: Comparable` means "any `T` that can be compared".
- **Subtyping and polymorphism:**
  - **Subtyping:** a `Dog` can be used where an `Animal` is expected.
  - **Subtype polymorphism:** calling a method runs the version for the value's actual type (virtual dispatch).
  - **Ad-hoc polymorphism:** different code for different types under one name (overloading, type classes).
- **Variance:** how subtyping flows through generic types.
  - **Covariant:** an `IEnumerable<Dog>` is an `IEnumerable<Animal>`, because the list only produces values.
  - **Contravariant:** an `Action<Animal>` is an `Action<Dog>`, because the action only consumes them.
  - **Invariant:** a `List<Dog>` is neither, because a list does both.
- **Traits / type classes / interfaces:**
  - **Interfaces (C#, Java):** declare a set of methods a type can promise to have; the type must say so where it's
    declared.
  - **Type classes (Haskell) and traits (Rust, Piper):** the same idea, but an implementation can be added after the
    type is declared, even for types you don't own, such as `int`.
- **Witness:** the evidence that a type satisfies a trait or constraint, in practice an object holding the
  implementations of the trait's methods for that type. Passing witnesses to generic code ("dictionary passing") is how
  Haskell compiles type classes. It's also how Piper lets `int`, `string` and .NET types implement Piper traits, which
  .NET's own interfaces can't do: see "How trait bounds are compiled" in [language.md](piper/docs/language.md).

## Functions and control flow

- **First-class functions:** functions are values that can be stored, passed and returned.
- **Higher-order function:** a function that takes or returns functions, such as `Map`, `Where` and `Sort(comparer)`.
- **Lambda (anonymous function):** a function written inline without a name: `x => x * 2`.
- **Closure:** a function together with the variables it captures from where it was created. In
  `let add = n => total += n;` the lambda captures `total`.
  - **Capture by reference:** the closure sees later changes to the variable. C# and Piper capture this way.
  - **Capture by value:** the closure copies the variable when it's created. C++ (`[=]`) and Rust (`move`) can capture
    this way.
- **The loop-capture trap:** with capture by reference, a lambda created in a loop must get *that iteration's*
  variable, or every lambda sees the last value. C# 5 changed `foreach` to give each iteration its own variable.
  Piper does the same for every `for`, including `for i in 0..<n`.
- **Trailing closure:** a block after a call that's passed as the call's last argument: `f(a, b) { x => body }` means
  `f(a, b, x => { body })`. Kotlin and Swift have them. They let library functions read like built-in statements
  (`Retry(3) { … }`), which is one way to extend a language without changing it.
- **Currying and partial application:**
  - **Currying:** turning a function of several arguments into a chain of one-argument functions:
    `add(1)(2)`.
  - **Partial application:** fixing some arguments to get a new function.
- **Expression-oriented language:** most constructs produce a value, so `let label = if n < 0 { "negative" } else
  { "positive" };` works. Rust, F#, Kotlin and Piper are expression-oriented; C# is mostly statement-oriented.
- **Pipe operator:** `x |> f |> g` means `g(f(x))`, so data flows left to right (F#, Elixir, Piper).
- **Iterators and generators:** functions that produce a sequence lazily, one item at a time, with `yield return`. The
  compiler rewrites the function into a **state machine** that remembers where it stopped.
- **async/await:** writing asynchronous code as if it were sequential. Each `await` is a point where the function can
  pause until a task completes. The compiler turns the function into a state machine whose steps run as results arrive.
- **Coroutines:** functions that can suspend and resume, the general idea behind generators and async functions.
- **Structured concurrency:** concurrent work is scoped to a block, which doesn't finish until everything started in
  it has. Piper's sample `concurrency { … }` plugin is a small example.
- **Actor model:** concurrency through actors, which share nothing and communicate only by messages. Erlang, Elixir,
  Akka and Orleans are built on it.
  - **Actor:** owns private state, receives messages in a **mailbox**, and handles them one at a time, so its state
    needs no locks.
  - **Tell and ask:** a *tell* sends a message without waiting; an *ask* waits for a reply.
  - **Supervision:** what happens when an actor fails, such as restarting it with fresh state. Erlang's "let it crash"
    philosophy is built on it.
  - **Location transparency:** an actor is used the same way whether it runs in the same process or on another
    machine, which makes actors a natural model for distributed systems.
  - **In Piper:** two plugins implement actors with the same syntax, one in-process on .NET channels and one across
    processes and machines on Proto.Actor. The same declarations run either way, which is location transparency
    achieved by changing a config file (see [piper/docs/plugins.md](piper/docs/plugins.md)).
- **Serialization:** turning a value into bytes (JSON, protobuf) to send or store it, and back.
  - **Self-describing data:** the receiver must know the type to rebuild. Proto.Actor's JSON sends a type name, so
    both ends must run the same program.
  - **Schema evolution:** what happens when one side's types change, which long-lived distributed systems must plan
    for.
- **Network failures:** a message across a network can be delayed, lost or duplicated, and the other side may have
  crashed. So remote calls need timeouts, and "at most once" or "at least once" delivery is a design choice. The
  "fallacies of distributed computing" list the assumptions (the network is reliable, latency is zero, …) that
  break here.
- **Message passing vs. shared memory:** the two broad ways for concurrent code to cooperate.
  - **Message passing:** send copies or immutable values (actors, Go's channels).
  - **Shared memory:** read and write the same data, protected by locks.

  Shared mutable data is the source of **data races**, where two threads change something at once.
- **Exceptions vs. error values:** exceptions jump out of any number of calls to a handler, and are invisible in
  function types. Error values (`Result`) are explicit, and must be checked or passed on.

## The middle and back end

- **Lowering:** rewriting high-level constructs into simpler ones the next stage understands. C#'s `foreach`, `using`,
  `async` and `yield` are all lowered before code generation. Piper's [Lowerer.cs](piper/src/Piper.Compiler/Lowering/Lowerer.cs)
  turns expression-valued `if`s and blocks into the statements C# needs.
- **Optimization:** making the generated code faster or smaller without changing what it does. Common passes:
  - **Constant folding:** computing `60 * 60 * 24` at compile time.
  - **Dead code elimination:** removing code that can never run or whose result is never used.
  - **Inlining:** replacing a call with the body of the called function.
  - **Common subexpression elimination:** computing a repeated expression once.
  - **Loop-invariant code motion:** moving work that doesn't change out of a loop.
  - **Escape analysis:** finding objects that never outlive a function, so they can live on the stack.
- **Code generation:** producing the target from the lowered program. Piper's is
  [CSharpEmitter.cs](piper/src/Piper.Compiler/Emit/CSharpEmitter.cs).
- **Linking:** combining compiled pieces and libraries into one program. .NET links at run time, loading assemblies on
  demand.
- **Garbage collection (GC):** automatic memory management that frees objects nothing refers to any more. The
  alternatives are manual memory management (C), reference counting (Swift) and ownership with borrowing (Rust).
- **Debug information and source mapping:** records that map generated code back to the original source, so stack
  traces and debuggers show the right lines. Piper writes `#line` directives into the C#, and TypeScript produces
  source maps.

## Extending a language

- **Macro:** code that runs at compile time to generate code.
  - **Textual macros** (C's `#define`) substitute text, and are easy to misuse.
  - **Syntactic macros** (Rust's `macro_rules!`, Lisp) work on syntax trees.
- **Hygiene:** a macro's own names can't clash with the user's. A hygienic macro that introduces a temporary variable
  can't capture or hide a user variable of the same name. Piper's plugins get hygiene by asking for fresh names
  (`__tasks_1`).
- **Quasi-quotation:** writing code templates with holes to fill in, as Lisp's backquote does. It's a much easier way
  to generate syntax than building trees node by node. Piper's `SyntaxTemplate` (`"Parallel.ForEach($items, $x => $body);"`)
  is an example.
- **Compiler plugins:** code loaded by the compiler to add features. Piper's plugins add keywords (`parallel for`,
  `services { }`, `endpoints { }`) and rewrite them into ordinary Piper as they're parsed, so the rest of the
  compiler, including type checking and the editor, needs no changes. See
  [piper/docs/plugins.md](piper/docs/plugins.md).
- **Source generators:** compiler plugins that add new source code but can't change existing code. C# has these.
- **Analyzers:** compiler plugins that inspect the type-checked program and report diagnostics without changing it,
  like Roslyn analyzers. Piper's plugins can do this too: the actors plugin warns when a message carries something
  mutable, a check that needs types, so it can't happen while parsing.
- **DSL (domain-specific language):** a small language for one job, such as SQL, regular expressions or build scripts.
  - **External DSL:** has its own syntax and parser.
  - **Internal (embedded) DSL:** written inside a host language using its features. Kotlin's builders and
    trailing-closure APIs are internal DSLs; so is code using Piper's `services` and `endpoints`.
- **Metaprogramming:** programs that read, generate or change programs. It covers macros, reflection, code generation
  and plugins.

## Tooling

- **Language server:** a process that gives editors language smarts: errors as you type, completion, hover,
  go-to-definition, rename and formatting.
- **LSP (Language Server Protocol):** a JSON protocol between editors and language servers, created for VS Code. One
  server then works in many editors. Piper's is in [piper/src/Piper.LanguageServer](piper/src/Piper.LanguageServer),
  and reuses the compiler's fault-tolerant parser and binder.
- **Semantic tokens / semantic highlighting:** colouring based on what names mean (a type, a parameter), supplied by
  the language server. **TextMate grammars** colour by regular expressions only.
- **REPL (read–eval–print loop):** an interactive prompt that runs code as you type it.
- **Formatter / linter:** tools that lay out code consistently, or warn about suspicious code.
- **Golden (snapshot) tests:** compiling a sample program and comparing the output with a saved "expected" file. They
  catch any change in generated code. Piper keeps them in [piper/tests/Piper.Tests/Golden](piper/tests/Piper.Tests/Golden).

## Design choices and trade-offs

Designing a language is mostly choosing between good options. Some of the questions every language answers:

- **Familiar or novel?** Familiar syntax and behaviour lower the cost of learning; novelty needs a reason. Piper
  defaults to whatever C# does unless that breaks null safety.
- **Mutable or immutable by default?** Rust, F# and Piper make `let` immutable and ask for `mut`. Immutability makes
  code easier to reason about, especially with concurrency.
- **Exceptions or error values?** (See above.) Many modern languages use error values for expected failures and keep
  exceptions (or panics) for bugs.
- **How much inference?** More inference means less typing but can make code harder to read and errors harder to
  explain. Most languages require types on function signatures and infer the rest.
- **Small core or many built-in features?** A small core with good extension mechanisms keeps the language easier to
  learn and maintain. That's Piper's direction, with trailing closures and plugins. Built-in features can give better
  errors and tooling.
- **Orthogonality:** features that combine freely without special cases. For example, "everything is an expression"
  means `if`, `match` and blocks work anywhere a value does.
- **Compatibility:** once people write code in a language, changing what it means breaks them. Reserving words early,
  rejecting unknown keys in config files, and versioning help a language evolve without breaking its users.
- **Interop:** being able to use an existing ecosystem (.NET's libraries, NuGet packages, ASP.NET Core) is often worth
  more than any single language feature.

## Further reading

- **Robert Nystrom, *Crafting Interpreters*:** free online. It builds a tree-walking interpreter and then a bytecode
  VM, explaining every step. The best place to start.
- **Thorsten Ball, *Writing an Interpreter in Go* and *Writing a Compiler in Go*:** short and practical.
- **Keith Cooper and Linda Torczon, *Engineering a Compiler*:** a thorough modern textbook.
- **Aho, Lam, Sethi and Ullman, *Compilers: Principles, Techniques, and Tools*:** "the Dragon Book", the classic
  reference.
- **Benjamin Pierce, *Types and Programming Languages*:** the standard introduction to type systems.
- **The LLVM Kaleidoscope tutorial:** building a small language on LLVM.
- **The Roslyn source code** (github.com/dotnet/roslyn): a real production compiler and language service, in C#.
