# The Piper language (milestone 7)

Piper is a small, C-style, expression-oriented language that transpiles to C#. It borrows
immutable-by-default bindings, block expressions, enums (sum types), exhaustive `match` and traits
from Rust, `let`/`var` and retroactive `extend` from Swift, and records, generics, string
interpolation and the `T?` / `?.` / `??` null-handling syntax from C#. There are no null references:
absence is the built-in `Option<T>`, and errors are values of the built-in `Result<T, E>`. Generated code needs C# 14 (.NET 10), which is used for extension members.

```piper
record Point(x: Int, y: Int);

fn add(a: Int, b: Int) -> Int = a + b;

fn classify(n: Int) -> String {
    if n < 0 { "negative" } else if n == 0 { "zero" } else { "positive" }
}

fn main() {
    let p = Point(1, 2);
    var total = 0;
    for i in 0..10 { total += i; }
    print("p=({p.x}, {p.y}) total={total} {classify(-3)}");
}
```

## Reference grammar

This grammar is **documentation**, not the source of truth: the hand-written parser in
[Parser.cs](../src/Piper.Compiler/Syntax/Parser.cs) is, and each `Parse*` method carries the rule
it implements. Keep the two in step when changing syntax.

Notation: `{ x }` is zero or more, `[ x ]` is optional, `|` separates alternatives, `"x"` is a literal token.

```ebnf
compilationUnit = { using } [ "namespace" qualifiedName ";" ] { using } { [ "private" ] declaration } EOF ;
using           = "using" qualifiedName ";" ;                (* a Piper or .NET namespace *)
qualifiedName   = IDENT { "." IDENT } ;
declaration     = function | record | enum | trait | extend ;

function        = [ "static" ] "fn" IDENT [ typeParameters ] "(" [ parameters ] ")" [ "->" type ]
                  ( block | "=" expression ";" | ";" ) ;      (* ";" only for required trait methods; "static" only in extend blocks *)
record          = "record" IDENT [ typeParameters ] "(" [ parameters ] ")" ";" ;
enum            = "enum" IDENT [ typeParameters ] "{" [ variant { "," variant } [ "," ] ] "}" ;
variant         = IDENT [ "(" [ parameters ] ")" ] ;
trait           = "trait" IDENT "{" { function } "}" ;
extend          = "extend" namedType [ ":" type { "," type } ] "{" { function } "}" ;
typeParameters  = "<" typeParameter { "," typeParameter } [ "," ] ">" ;
typeParameter   = IDENT [ ":" type { "+" type } ] ;           (* bounds must be traits *)
parameters      = parameter { "," parameter } [ "," ] ;
parameter       = IDENT ":" type ;
type            = ( namedType | functionType | tupleType ) { "?" | "[" { "," } "]" } ;   (* T? is Option<T>; Int[] an array, Int[,] 2-D *)
functionType    = "(" [ type { "," type } [ "," ] ] ")" "->" type ;   (* (Int) -> Bool is Func<int, bool> *)
tupleType       = "(" tupleElement "," tupleElement { "," tupleElement } ")" ;
tupleElement    = [ IDENT ":" ] type ;                        (* (x: Int, y: Int) *)
namedType       = qualifiedName [ "<" type { "," type } [ "," ] ">" ] ;   (* e.g. System.Text.StringBuilder, List<Int> *)

block           = "{" { statement } [ expression ] "}" ;      (* final expression = block's value *)
statement       = variableDecl | while | for | return | try | "break" ";" | "continue" ";"
                | blockLike [ ";" ] | expression ";" | ";" ;
blockLike       = if | match | block ;                        (* ends at its '}', no ';' needed *)
variableDecl    = ( "let" | "var" ) ( IDENT [ ":" type ] | designation ) "=" expression ";" ;
designation     = "(" ( IDENT | designation ) "," ( IDENT | designation ) { "," ( IDENT | designation ) } ")" ;   (* "_" discards *)
while           = "while" expression block ;
for             = "for" ( IDENT | designation ) "in" expression block ;   (* a range, or anything enumerable *)
try             = "try" block { catch } [ "finally" block ] ;   (* at least one catch or a finally *)
catch           = "catch" [ "(" IDENT [ ":" type ] ")" ] block ;   (* no type: any exception *)
return          = "return" [ expression ] ";" ;

expression      = assignment ;
assignment      = binary [ ( "=" | "+=" | "-=" | "*=" | "/=" | "%=" ) assignment ] ;
binary          = unary { binaryOp unary | "is" pattern } ;   (* precedence table below *)
unary           = ( "-" | "!" ) unary | postfix ;
postfix         = primary { "(" [ arguments ] ")" | "[" arguments "]" | "." IDENT [ typeArguments ] | "?" | "?." IDENT postfix } ;
                                                              (* after "?." the rest of the chain is skipped on None *)
arguments       = argument { "," argument } [ "," ] ;
argument        = expression | "out" [ "let" | "var" ] IDENT ;   (* out: only for .NET out parameters *)
primary         = INT | DOUBLE | CHAR | STRING | "true" | "false" | "null" | IDENT [ typeArguments ] | "this"
                | "(" expression ")" | tuple | block | if | match | lambda | collection | "throw" expression ;
typeArguments   = "<" type { "," type } ">" ;               (* only where C#'s rule allows; see below *)
tuple           = "(" [ IDENT ":" ] expression "," [ IDENT ":" ] expression { "," [ IDENT ":" ] expression } ")" ;
lambda          = ( IDENT | "(" [ lambdaParam { "," lambdaParam } [ "," ] ] ")" ) "=>" ( block | expression ) ;
lambdaParam     = IDENT [ ":" type ] ;                        (* types may come from the expected function type *)
collection      = "[" [ arguments ] "]" ;
if              = "if" expression block [ "else" ( block | if ) ] ;

match           = "match" expression "{" { arm ( "," | (* optional after a blockLike body *) ) } "}" ;
arm             = pattern [ "if" expression ] "=>" armBody ;
armBody         = expression | "return" [ expression ] | "break" | "continue" ;

pattern         = primaryPattern { "|" primaryPattern } ;
primaryPattern  = "_"                                         (* wildcard *)
                | "null"                                      (* None *)
                | "(" pattern "," pattern { "," pattern } ")"     (* tuple *)
                | IDENT                                       (* fieldless variant, else a binding *)
                | [ IDENT "." ] IDENT [ "(" [ pattern { "," pattern } [ "," ] ] ")" ]   (* variant / record *)
                | literalPattern [ ( ".." | "..=" ) literalPattern ] ;
literalPattern  = [ "-" ] ( INT | DOUBLE ) | CHAR | STRING | "true" | "false" ;
```

### Operator precedence (highest first)

| Level | Operators | Associativity |
|---|---|---|
| 10 | postfix `()` `[]` `.` `?.` `?` | left |
| 9 | unary `-` `!` | prefix |
| 8 | `*` `/` `%` | left |
| 7 | `+` `-` | left |
| 6 | `<` `<=` `>` `>=` `is` | left |
| 5 | `==` `!=` | left |
| 4 | `&&` | left |
| 3 | `\|\|` | left |
| 2 | `??` | right |
| 1 | `..` `..=` | left (only valid in `for`) |
| 0 | `=` `+=` `-=` `*=` `/=` `%=` | right |

The table lives in [SyntaxFacts.cs](../src/Piper.Compiler/Syntax/SyntaxFacts.cs); changing a
number there is all it takes to change precedence.

**Type arguments or less-than?** As in C#, a `<` after a name starts type arguments only if the tokens up to the
matching `>` can be types and the token after it is one of `( ) ] } : ; , . ?. ? == != | && || [`. So
`parse<Int>(s)` and `List<Int>()` are generic, `a < b && c > d` is two comparisons, and `f(a < b, c > (d))` is a
generic call `a<b, c>(d)`, as in C#.

### Lexical structure

- **Identifiers:** ASCII letters, digits and `_`, not starting with a digit.
- **Keywords:** `fn let var if else while for in return break continue record enum match trait extend static this null is namespace using private try catch finally throw out true false`.
- **Comments:** `// line` and `/* block */`. Block comments nest.
- **Integers:** `42`, `1_000_000`, `0xFF`, `0b1010`. Add an `L` suffix for Long: `3_000_000_000L`. An unsuffixed literal must fit in Int; as in C#, `-2147483648` and `-9223372036854775808L` are allowed too.
- **Doubles:** `1.5`, `2e3`, `2.5E-1`. A digit is required after the `.`, so `0..10` is a range.
- **Chars:** `'a'`, `'\n'`.
- **Strings:** `"..."`, with escapes `\n \t \r \0 \\ \" \' \{ \} \u{1F99C}`.
- **Interpolation:** `{expression}` inside a string, for example `"sum={a + b}"`. Write `\{` for a literal brace.

## Semantics

### Types

The builtin types are `Int`, `Long`, `Double`, `Bool`, `String`, `Char` and `Unit`. Unit is the type of
expressions evaluated only for their effect, and is written as C# `void`.
- **Records:** `record Point(x: Int, y: Int);` declares an immutable value type with value equality. Construct one with `Point(1, 2)` and read fields with `p.x`.
- **Implicit conversions:** only widenings are implicit: Int → Long, Int → Double, Long → Double. Narrowing is an error.
- **Arithmetic:** mixed numeric operands are promoted to the wider type, so `1 + 2.5` is a Double. `/` on integers truncates.
- **Never:** the internal type of a block that always returns, breaks or continues. It fits wherever any type is expected, so `let x = if ok { 1 } else { return; };` type-checks.
- **Overflow:** integer arithmetic wraps around on overflow, as in C#'s default (unchecked) context: with `let max = Int.MaxValue;`, `max + 1 == Int.MinValue`.
- **Tuples and arrays:** `(Int, String)` is a tuple and `Int[]` an array, both as in C#; see [Tuples](#tuples-and-deconstruction) and [Collections](#collections).

### Enums

An enum is a sum type: each value is exactly one of its variants, and a variant may carry fields.

```piper
enum Shape {
    Circle(radius: Double),
    Rect(width: Double, height: Double),
    Empty,
}

let c = Shape.Circle(2.0);   // variants with fields are called like functions
let e = Shape.Empty;         // fieldless variants are written without parentheses
```

- **Variants are not types.** `Shape.Circle(2.0)` has type `Shape`.
- **Enums can be recursive**, e.g. `enum Expr { Num(value: Int), Add(left: Expr, right: Expr) }`.
- **Values compare structurally** with `==`.
- **The only way to look inside a value is `match`.** There is no field access on enum values.

### Pattern matching

`match` tests a value against each arm's pattern in order and evaluates the first arm that matches.
Like `if`, it is an expression.

```piper
fn area(s: Shape) -> Double = match s {
    Circle(r) => 3.14159 * r * r,
    Rect(w, h) if w == h => w * w,      // a guard
    Rect(w, h) => w * h,
    Empty => 0.0,
};
```

| Pattern | Matches |
|---|---|
| `_` | anything |
| `name` | anything, binding it to `name` (immutable) |
| `Circle(p)`, `Shape.Circle(p)` | that variant, matching its fields against the sub-patterns |
| `Empty`, `Shape.Empty` | a fieldless variant; a bare name is a variant if the matched enum has one by that name, otherwise a binding |
| `Point(x, 0)` | a record, deconstructed positionally |
| `42`, `-1`, `2.5`, `'c'`, `"text"`, `true` | that literal; an Int literal also matches Long and Double values |
| `1..10`, `'a'..='z'` | a range of Int, Long or Char values; `..` excludes the end, `..=` includes it |
| `p1 \| p2` | either pattern (alternatives cannot bind variables yet) |

Patterns nest to any depth, e.g. `Add(Num(0), x)`.

- **Exhaustiveness:** a match must handle every possible value, or it is a compile error that names a missing case, e.g. `'Shape.Rect(_, _)' is not covered`.
  - Enums, records and Bool are checked exactly.
  - Int, String and the other types with unlimited values need a final `_` or binding arm.
  - Guarded arms don't count towards coverage.
- **Unreachable arms:** an arm that can never match, because earlier unguarded arms cover everything it would, is a warning. A misspelled variant name usually shows up this way: it becomes a binding that matches everything, so every later arm is unreachable.
- **Arm types:** the match's type is the arms' common type, unified as for `if`.
- **Jumps in arms:** an arm may also be `return …`, `break` or `continue`. These have type Never and jump out of the enclosing function or loop.
- **Guards:** guards are Bool expressions evaluated after the pattern matches, with its bindings in scope. A guard cannot (yet) contain a block with statements or a nested `match`.

### Generics

Records, enums, functions and methods can take type parameters.

```piper
record Pair<A, B>(first: A, second: B);
enum Option<T> { Some(value: T), None }

fn swap<A, B>(p: Pair<A, B>) -> Pair<B, A> = Pair(p.second, p.first);
```

- **Inferred type arguments:** type arguments are usually inferred from the arguments, e.g. `Pair(1, "one")` is a `Pair<Int, String>`.
- **Explicit type arguments:** they can also be written, as in C#: `empty<String>()`, `identity<Long>(1)`, `Pair<Long, String>(1, "x")`, `Option<Int>.None`, `Stack<Int>.empty()`.
- **The expected type helps.** When the arguments don't settle a type argument, the expected type does: `let x: Option<Int> = Option.None;` or `fn f() -> Option<Int> = Option.None;`. If neither settles it, you get an error asking for a type annotation.
- **Numeric widening applies:** `let p: Pair<Long, Double> = Pair(1, 2);` converts the arguments.
- **Bounds:** `fn largest<T: Shape>(a: T, b: T) -> T` requires `T` to implement the `Shape` trait, so `a.area()` can be called. Write several bounds as `T: A + B`. Bounds are checked wherever a type is instantiated.
- **No equality on type parameters:** `==` isn't available on type parameters yet.
- **Exhaustiveness sees through type arguments:** `match o { Some(true) => …, None => … }` on an `Option<Bool>` reports `'Option.Some(false)' is not covered`.

### Traits

A trait is a named set of methods, like a Rust trait or a Swift protocol. A method without a body must be
implemented; a method with a body is a default that implementers may override.

```piper
trait Shape {
    fn area() -> Double;
    fn describe() -> String = "area {this.area()}";
}

record Circle(radius: Double);

extend Circle: Shape {
    fn area() -> Double = 3.14159 * this.radius * this.radius;
}
```

- **Implementing a trait:** `extend Type: Trait1, Trait2 { … }` declares that a record or enum implements traits. Every required method must be present, with exactly the trait's signature.
- **Traits as types:** a trait is also a type. A `Circle` converts implicitly to `Shape`, and calls through a `Shape` value dispatch dynamically.
- **Traits as bounds:** a type parameter bound by a trait gets static dispatch.
- **Using the expected type:** when an `if` or `match` produces different types that all implement the expected trait, the trait is used, e.g. `let s: Shape = if c { circle } else { rect };`.
- **Not yet supported:** traits can't be generic, inherit from each other, have static methods or refer to the implementing type as a type (a `Self` type). Builtin types can't implement traits.

### Methods and `extend`

`extend` adds methods to an existing type: a record, an enum, or a builtin such as `Int` or `String`.

```piper
extend Int {
    fn squared() -> Int = this * this;
    static fn zero() -> Int = 0;          // a static method, called as Int.zero()
}

extend Option {                           // generic types: their type parameters are in scope
    fn getOr(fallback: T) -> T = match this { Some(v) => v, None => fallback };
}

let n = 7.squared();
let s = Option.Some(5).getOr(0);
```

- **Instance and static methods:** as in C#, a method is an instance method by default, with `this` referring to the receiver. Mark it `static fn` to call it on the type instead, e.g. `Int.zero()`. There is no explicit receiver parameter.
- **`this` is immutable.** Using it in a static method or a top-level function is an error.
- **Generic types:** write `extend Option`, not `extend Option<Int>`. Methods apply to every instantiation.
- **Name clashes are errors.** A method can't share a name with a field, a variant, a variant's field (variants inherit their enum's methods) or another method.
- **Builtin members:** on builtin types, a method can't reuse a name the .NET type already has (such as `String.Length`), because the .NET member would silently win.

### Null safety: `Option` and `T?`

Piper has no null references. A value that might be absent has the built-in type `Option<T>`, written
`T?` as in C#, so the compiler makes every use deal with the missing case.

```piper
enum Option<T> { Some(value: T), None }      // built in; Some(…) and None can be used unqualified
```

| Syntax | Meaning |
|---|---|
| `let x: Int? = 5;` | implicit `Some`: a plain value converts to its optional type |
| `let y: Int? = null;` / `return null;` | `null` is `None`; it only type-checks where an optional type is expected |
| `user?.name` | `Some(user.name)` if `user` has a value, else `None`. The whole chain after `?.` is skipped, and an already-optional result isn't wrapped again (`user?.email` is `String?`, not `String??`) |
| `user?.greet();` | as a statement, the call only happens when there is a value |
| `name ?? "anonymous"` | the value, or the fallback (evaluated only if needed); right-associative, so `a ?? b ?? c` works |
| `let u = find(id)?;` | the value, or **return `None` from the enclosing function** (which must return an optional) |
| `x == null`, `x != null`, `x == 5` | comparisons; `x == 5` compares with `Some(5)` |
| `if x is Some(n) && n > 3 { … n … }` | C#-style `is` patterns. Variables bound in an `if`/`while` condition (or a match guard) are in scope in the body |
| `match x { null => …, Some(n) => … }` | `null` is also a pattern for `None`; exhaustiveness checks apply |

- **Inference through `T?`:** `fn f<T>(v: T?)` can be called as `f(5)`, which infers `T = Int` and wraps the argument in `Some`.
- **`is` patterns elsewhere:** outside a condition, an `is` pattern can test but not bind, e.g. `let ok = x is Some(_);`.

### Errors as values: `Result`

```piper
enum Result<T, E> { Ok(value: T), Err(error: E) }    // built in; Ok(…) and Err(…) can be used unqualified

fn compute(a: Int, b: Int) -> Result<Int, MathError> {
    let q = divide(a, b)?;      // an Err returns from compute immediately
    Ok(q * 10)
}
```

- **`?` propagates errors.** On a `Result`, `?` unwraps `Ok`, or returns the `Err` from a function that itself returns a `Result` with a compatible error type.
- **Pattern matching:** `Result` values match like any enum, e.g. `Err(MathError.DivideByZero)`.
- **No redeclaring:** `Option` and `Result` are built in, so they can't be redeclared. You can extend them, e.g. `extend Option { fn isNone() -> Bool = this is None; }`.

### Programs, namespaces and visibility

A program is any number of `.piper` files compiled together, e.g. `piper run src/`. As in C#, each file
may start with a file-scoped namespace and `using` directives:

```piper
namespace Geometry;          // everything in this file is in Geometry

using System.Text;           // .NET namespaces...
using Geometry.Shapes;       // ...and Piper ones alike

record Point(x: Double, y: Double);
fn area(s: Shape) -> Double = ...;
private fn square(x: Double) -> Double = x * x;   // visible only in this file
```

- **Namespaces span files.** Several files can declare the same namespace and see each other's declarations without a `using`. A file without a `namespace` contributes to the global namespace.
- **Qualified names always work**, without a `using`, in both expressions and types: `Geometry.area(s)`, `Geometry.Point(1.0, 2.0)`, `System.Text.StringBuilder`.
- **`using N;` imports** N's types and functions, but not its sub-namespaces, as in C#. A name imported from two namespaces is an error where it's used; qualify it.
- **Lookup order:** this file's private declarations, then the file's namespace and the namespaces enclosing it, then Piper's builtins, then imports.
- **Visibility:**
  - Declarations are public by default.
  - `private` restricts a function, record, enum, trait or extend-block method to its own file.
  - A public declaration can't expose a private type in its signature or fields. Make it private too, or make the type public.
- **`extend` works across files.** An `extend` block can extend a type declared in another file (the emitted C# uses `partial` types).
- **One entry point:** exactly one `main`, in any namespace.

### .NET interop

Any public type in the .NET base libraries can be used. Builtin Piper types are the corresponding .NET types,
so their members are available too.

```piper
using System;
using System.Text;
using System.Collections.Generic;

let root = Math.Sqrt(16);                  // static method (Int widens to Double)
let max = Int.MaxValue;                    // static field
let shout = "piper".ToUpper();             // instance method on a builtin type
let length = shout.Length;                 // property
let builder = StringBuilder();             // constructor: call the type, as for Piper records
builder.Append("x = ");
builder.Append(42);                        // overload chosen by argument type

let points: List<Point> = List();          // generic types take their type arguments from the expected type
points.Add(Point(1, 2));                   // and can hold Piper values
```

- **Overload resolution** follows C#, simplified:
  - An exact match beats a numeric widening (Int → Long beats Int → Double).
  - That beats a conversion to a base type or interface, which beats `object`.
  - Ties go to the normal form over a `params` expansion, then to the overload needing fewer default arguments. Anything still tied is reported as ambiguous.
- **Null at the boundary:**
  - .NET members whose results are annotated as possibly null, and `Nullable<T>` values, come back as `T?`, with `null` becoming `None`. For example, `Environment.GetEnvironmentVariable(name) ?? "unset"`.
  - Where .NET accepts null, you can pass a `T?`, and `None` becomes `null`.
- **`extend` on .NET types:** a non-generic .NET type can be extended like a builtin, e.g. `extend StringBuilder { fn line(s: String) { this.AppendLine(s); } }`, but not with a member name it already has.
- **Generic and extension methods:** type arguments are inferred from the arguments, including lambdas, and extension methods from imported namespaces apply, so LINQ works: `numbers.Where(n => n > 2).Select(n => n * 10).ToList()`.
- **Indexers and setters:** `list[0]`, `ages["Ada"] = 36`, `ages["Ada"] += 1` and `builder.Length = 0` use .NET indexers, arrays and property setters.
- **Explicit type arguments** work for .NET too: `Dictionary<String, Int>()`, `Enumerable.Empty<Int>()`, `xs.Select<Int, String>(…)`.
- **`out` parameters,** as in C#: `out let n` declares `n` (`out var n` declares it mutable), `out n` assigns an existing `var` of exactly the parameter's type, and `out _` discards. A variable declared in an `if` condition stays in scope after the `if`, as in C#, so guards work:
  ```piper
  if !Int.TryParse(text, out let n) { return; }
  print(n * 2);
  ```
  Where .NET may write null (`out string?`, or `[MaybeNullWhen(false)]` as on `Dictionary.TryGetValue`), the variable is optional: `if names.TryGetValue(key, out let name) { print(name ?? "?"); }`. Piper functions have no `out` parameters; they return several values as a [tuple](#tuples-and-deconstruction).
- **`in` parameters** take ordinary arguments.
- **Not yet supported:** `ref` parameters and events.

### Lambdas and function values

A function type `(Int, Int) -> Bool` is the .NET delegate `Func<int, int, bool>`, and one returning Unit is an
`Action`. They pass straight to .NET APIs, and other delegate types (`Comparison<T>`, `Predicate<T>`) are accepted where expected.

```piper
let increment: (Int) -> Int = x => x + 1;     // parameter types come from the expected type
let add = (a: Int, b: Int) => a + b;          // or from annotations: add is (Int, Int) -> Int
let total = apply(double, 21);                // a top-level function is a value too
let twice: (Int) -> Int = identity;           // a generic one takes its type arguments from the expected type
let echo = identity<String>;                  // or from explicit ones
names.Sort((a, b) => a.Length - b.Length);    // a .NET delegate type
```

- A lambda's return type is the expected one, or else the common type of its body's value and its `return`s.
- Lambdas capture variables by reference, as in C#, so `n => { total += n; }` updates `total`.
- A lambda without annotations needs an expected function type; a lambda with too few or many parameters for it is an error.

### Collections

- **Literals:** `[1, 2, 3]` is a `List<Int>`. The element type comes from the expected type (`List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, … or a .NET array) or else the elements' common type. `[]` needs an expected type.
- **`for x in collection`** works on anything implementing `IEnumerable<T>`: lists, arrays, strings (as `Char`s), dictionaries and LINQ results.
- **Indexing:** `xs[i]` on arrays and on any .NET type with an indexer.
- **Arrays:** `Int[]` is an array of Int and `Int[,]` a two-dimensional one, as in C#. `let xs: Int[] = [1, 2, 3];` makes one from a literal, and `Int[5]` or `Double[2, 3]` creates one of default values, like C#'s `new int[5]`. That needs an element type whose default isn't null (numbers, Bool, Char, optionals, .NET structs and tuples of those): `String[3]` is an error, as its elements would be null, but `String?[3]` (three `None`s) is fine.

### Tuples and deconstruction

Tuples are C#'s value tuples. Elements can be named, and are read by name or as `Item1`, `Item2`, ….

```piper
fn divide(a: Int, b: Int) -> (quotient: Int, remainder: Int) = (a / b, a % b);

let result = divide(17, 5);
print("{result.quotient} r {result.remainder}");
let (q, r) = divide(7, 2);                    // deconstruction: tuples, records, .NET types with Deconstruct
let (x, (_, z)) = (1, (2, 3));                // nested; _ discards
(a, b) = (b, a);                              // into existing vars: the right side is evaluated first
for (name, age) in ages { … }                 // a Dictionary's KeyValuePairs deconstruct
match cell { (0, 0) => "origin", (0, _) | (_, 0) => "axis", _ => "elsewhere" }
```

- **Names** are inferred from variables and members, as in C#: `(name, p.x)` has elements `name` and `x`. They're part of the type but not of its identity: `(x: Int, y: Int)` and `(Int, Int)` convert to each other.
- **Conversions** go element by element: `let t: (Long, Int?) = (1, 2);`.
- **Equality:** `==` compares element by element, for elements that support `==`.
- **Records** deconstruct into their fields, in order: `let (x, y) = point;`.
- **Patterns:** `(p1, p2)` matches a tuple, and exhaustiveness checks it like a record: `match (a, b) { (true, true) => …, (true, false) | (false, true) => …, (false, false) => … }` is exhaustive.
- A tuple has at least two elements; .NET methods taking or returning tuples work up to seven.

### Exceptions

Exceptions are .NET exceptions, for code that crosses into .NET; within Piper, prefer `Result`.

```piper
try {
    print(divide(1, 0));
} catch (e: ArgumentException) {
    print("caught: {e.Message}");
} catch (_) {                                 // no type: any exception; `_` binds nothing
    print("something else");
} finally {
    print("always");
}
```

- `throw e` throws any value deriving from `System.Exception`. It is an expression of type Never, so it fits anywhere a value is expected: `if ok { v } else { throw InvalidOperationException("…") }`.
- `catch (e)` without a type catches `System.Exception`. A clause made unreachable by an earlier one is an error, as in C#.
- `break`, `continue` and `return` can't leave a `finally` block.

### Bindings

- `let` declares an immutable binding and `var` a mutable one. Assigning to a `let` binding, a parameter or a `for` loop variable is a compile error.
- Shadowing is allowed in nested scopes (`let x = x + 1;` inside a block), but redeclaring a name in the same scope is not.
- Declarations at the top level can appear in any order.

### Expressions everywhere

- **Blocks are expressions.** A block's value is its final expression, written without a `;`: `let y = { let t = 2; t * 3 };`.
- **`if` is an expression.** `let s = if c { "a" } else { "b" };` gives both branches' common type. An `if` without `else` has type Unit.
- **Function bodies follow the same rule.** A function body's final expression is its return value. A function with a return type must end in a value or return on every path.
- **Assignment is a statement-like expression of type Unit,** so `a = b = c` is rejected.
- **Conditions must be `Bool`.** There is no truthiness.

### Evaluation order

Evaluation is strictly left to right, including operands that contain blocks.
- `x += e` reads `x` before evaluating `e`.
- The right-hand side of `&&` or `||` is evaluated only when needed.
- The upper bound of `for i in a..b` is evaluated once.

### Builtins

`print(value)` writes any value followed by a newline, using its C# `ToString`: Bools print as `True`/`False`,
records as `Point { x = 1, y = 2 }` and tuples as `(1, a)`. Output is locale-independent. `main()` takes no parameters and returns Unit; it is the program's entry point.

## How Piper maps to C#

| Piper | C# |
|---|---|
| `record P(x: Int)` | `public sealed record P(int x);` |
| `enum Shape { Circle(r: Double), Empty }` | `public abstract record Shape` with a private constructor and nested `public sealed record Circle(double r) : Shape;`, `public sealed record Empty() : Shape;` |
| `Shape.Circle(1.0)` | `new Shape.Circle(1.0)` |
| `match s { Circle(r) if r > 1.0 => a, Empty => b, … }` | `if (s is Shape.Circle(var r) && r > 1.0) { … } else if … else { … }` |
| patterns `1 \| 2`, `1..=9`, `_` | C# patterns `1 or 2`, `>= 1 and <= 9`, `_` |
| `record Pair<A, B: Show>(…)` | `public sealed record Pair<A, B>(…) where B : Show` |
| `swap(p)` (generic) | `swap<int, string>(p)`: type arguments are always explicit |
| `(Int, String)`, `(x: Int, y: Int)`, `(1, "a")`, `t.x` | `(int, string)`, `(int x, int y)`, `(1, "a")`, `t.Item1` (always by position) |
| `let (a, b) = t;`, `(a, b) = (b, a);`, `for (k, v) in d` | `var (a, b) = t;`, `(a, b) = (b, a);`, `foreach (var (k, v) in d)` |
| `Int[]`, `Int[5]`, `Double[2, 3]` | `int[]`, `new int[5]`, `new double[2, 3]` |
| `Int.TryParse(s, out let n)` | `int n;` before the statement, then `int.TryParse(s, out n)`; an optional `out` goes through `PiperInterop.AfterOutReference(…)` |
| `trait Shape { fn area() -> Double; fn d() -> String = …; }` | `public interface Shape { double area(); string d() => …; }` |
| `extend Circle: Shape { fn area() … }` | members inside `public sealed record Circle(…) : Shape { public double area() … }` |
| `c.d()` where `d` is an un-overridden default | `((Shape)c).d()` (C# only exposes default interface methods through the interface) |
| `extend Int { fn squared() … static fn zero() … }` | C# 14 `extension(int self) { public int squared() … }` and `extension(int) { public static int zero() … }` |
| `this` | `this` in members; in C# extension blocks the receiver parameter is named `self`, since C# reserves `this` |
| `Int?`, `Result<Int, E>` | `Option<int>`, `Result<int, E>`: small `readonly record struct`s added to the output when used (not C#'s `int?`, which can't represent `None` for an unconstrained generic `T` or nest) |
| `Some(x)`, `None`, `null` | `Option<int>.Some(x)`, `Option<int>.None` |
| `match o { Some(v) => …, None => … }`, `o is Some(v)` | property patterns `o is { IsSome: true, Value: var v }`, `{ IsSome: false }` |
| `a ?? b`, `a?.b` | `a.IsSome ? a.Value : b`, `a.IsSome ? Option<T>.Some(a.Value.b) : Option<T>.None` (non-variable operands are first stored in a temporary) |
| `let x = f()?;` | `var t = f(); if (!t.IsSome) { return Option<R>.None; } x = t.Value;` |
| a `.piper` file | a `.cs` file of the same name; the file with `main` also gets the entry point, Option/Result and `PiperInterop` helpers |
| `namespace Geometry;` | `namespace Geometry { … }`; names from other namespaces are written `global::Geometry.Point` |
| `private record R(…)` / `private trait T` | `file sealed record R(…)` / `file interface T` (C# 11 file-local types) |
| `private fn f()` / private method | `internal static` in the file's module class / `internal` member |
| `extend` of a type declared in another file | that type becomes `partial`; the other file holds a `partial record` part with the added members |
| `Math.Sqrt(2.0)`, `StringBuilder()`, `s.Length` | `global::System.Math.Sqrt(2.0)`, `new global::System.Text.StringBuilder()`, `s.Length` |
| a possibly-null .NET result / a `T?` passed to .NET | `PiperInterop.FromNullableReference(…)` / `PiperInterop.ToNullableReference(…)` (and `…Value` versions for `Nullable<T>`) |
| `fn f(a: Int) -> Int { … }` | `public static int f(int a) { … }` in a static module class |
| `fn f(a: Int) -> Int = e;` | `public static int f(int a) => e;` |
| `let x = e;` / `var x = e;` | `int x = e;` (immutability is checked by Piper, not C#) |
| `if` expression, simple branches | `c ? a : b` |
| `if` expression, branches with statements | a variable assigned in each branch, or `return` in each branch |
| `for i in a..b` / `a..=b` | `for (int i = a; i < b; i++)` / `<=` (non-constant bounds hoisted) |
| `for x in xs` | `foreach (int x in xs)` |
| `[1, 2]` | `new List<int> { 1, 2 }` (or `new T[] { … }` for an expected array), which needs no target type, unlike C#'s `[1, 2]` |
| `(Int) -> Bool`, `x => x > 0` | `Func<int, bool>`, `(int x) => x > 0`: parameter types are always written |
| `f` used as a value | the method group `f` |
| `xs.Where(n => n > 2)` (extension method) | `Enumerable.Where<int>(xs, (int n) => n > 2)`: called statically, type arguments explicit |
| `try`/`catch (e: E)`/`finally`, `throw e` | `try`/`catch (E e)`/`finally`, `throw e;` (or a C# throw expression in `?:`) |
| `"…{e}…"` | `$"…{e}…"` |
| `print(x)` | `global::System.Console.WriteLine(x)` |

- **Locals** get unique C# names (`x`, `x_1`, …), because C# forbids shadowing.
- **Identifiers that are C# keywords** are escaped (`@class`).
- **The module class** is named after the source file (`hello.piper` → `Hello`). A `PiperEntryPoint.Main` calls `main`.

**Why `match` becomes an if/else chain, not a C# `switch`.**
- **No double-checking:** Piper checks exhaustiveness and reachability itself. C# treats a switch arm it considers subsumed as an error, and its analysis isn't identical to Piper's.
- **Jumps:** a `break` inside a C# `switch` statement would exit the switch rather than the enclosing loop.
- **Shared lowering:** the chain reuses the lowering already used for `if`, which handles arms with statements, returns and values.

The match is known to be exhaustive, so a final arm without a guard or bindings becomes a plain `else`. Otherwise the chain ends in `throw new UnreachableException()`.

## Known limitations

- Constant expressions that overflow, such as `Int.MaxValue + 1`, are rejected by the C# compiler rather than reported by Piper.
- Match guards can't contain statements, and `|` alternatives can't bind variables.
- Overlapping literal and range patterns (`1..=5` followed by `3`) aren't reported as unreachable.
- Traits: no generic traits, trait inheritance, static trait methods or `Self` type. Builtin types can't implement traits.
- `==` isn't defined on type parameters or trait-typed values.
- A function ending in `while true { … }` is still reported as "not all code paths return a value".
- `?` followed by member access needs parentheses, `(f()?).name`, because `?.` is conditional access.
- A match guard, or the rest of an if/while condition after an `is` that binds variables, can't contain `?`, or `?.`/`??` on anything but a variable.
- .NET interop: no `ref` parameters or events yet. Element names of tuples returned by .NET (`Math.DivRem`'s `Quotient`) aren't read; use `Item1` or deconstruct.
- An `out` parameter marked `[MaybeNullWhen(false)]` gives an optional variable even when the call returned true.

## Roadmap

1. ~~**Sum types and `match`**~~, done in milestone 2.
2. ~~**Null safety and errors**~~, done in milestone 4: `Option`/`T?`, `Result`, `null`, `?.`, `??`, `?` and `is` patterns.
3. ~~**Traits, generics and extensions**~~, done in milestone 3.
4. ~~**Modules, imports and .NET interop**~~, done in milestone 5: `namespace`, `using`, `private`, multi-file programs, and calling .NET.
5. ~~**Collections and lambdas**~~, done in milestone 6: lambdas and function types, LINQ, collection literals, `for x in collection`, indexing, property setters, and `try`/`catch`/`throw`.
6. ~~**Language freeze**~~, done in milestone 7: C#-compatible printing and minimum-value literals, arrays (`Int[]`, `Int[5]`), explicit type arguments, generic functions as values, tuples and deconstruction, and `out` parameters.
7. **Robustness and tooling** (milestone 8): `#line` mapping to `.piper` sources, treating any C# compile error as a compiler bug, larger sample programs, `using` for `IDisposable`, and packaging as a `dotnet tool`.
8. **Smaller features:** `defer`, async/await, and a pipe operator `|>`.
9. **A language server** that reuses the fault-tolerant parser and binder.
