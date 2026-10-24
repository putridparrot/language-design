# The Piper language (milestone 8)

Piper is a small, C-style, expression-oriented language that transpiles to C#. It borrows
immutable-by-default bindings, block expressions, enums (sum types), exhaustive `match` and traits
from Rust, `let`/`var` and retroactive `extend` from Swift, and records, generics, string
interpolation and the `T?` / `?.` / `??` null-handling syntax from C#. There are no null references:
absence is the built-in `Option<T>`, and errors are values of the built-in `Result<T, E>`. Generated code needs C# 14 (.NET 10), which is used for extension members.

```piper
record Point(x: int, y: int);

fn add(a: int, b: int) => int = a + b;

fn classify(n: int) => string {
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

function        = [ "static" ] [ "async" ] "fn" IDENT [ typeParameters ] "(" [ parameters ] ")" [ "=>" type ]
                  ( block | "=" expression ";" | ";" ) ;      (* ";" only for required trait methods; "static" only in extend blocks *)
record          = "record" IDENT [ typeParameters ] "(" [ parameters ] ")" ";" ;
enum            = "enum" IDENT [ typeParameters ] "{" [ variant { "," variant } [ "," ] ] "}" ;
variant         = IDENT [ "(" [ parameters ] ")" ] ;
trait           = "trait" IDENT [ typeParameters ] [ ":" type { "," type } ] "{" { function } "}" ;   (* ": Named" inherits *)
extend          = "extend" namedType [ ":" type { "," type } ] "{" { function } "}" ;
typeParameters  = "<" typeParameter { "," typeParameter } [ "," ] ">" ;
typeParameter   = IDENT [ ":" type { "+" type } ] ;           (* bounds must be traits *)
parameters      = parameter { "," parameter } [ "," ] ;
parameter       = IDENT ":" type ;
type            = ( namedType | functionType | tupleType ) { "?" | "[" { "," } "]" } ;   (* T? is Option<T>; int[] an array, int[,] 2-D *)
functionType    = "(" [ type { "," type } [ "," ] ] ")" "=>" type ;   (* (int) => bool is Func<int, bool> *)
tupleType       = "(" tupleElement "," tupleElement { "," tupleElement } ")" ;
tupleElement    = [ IDENT ":" ] type ;                        (* (x: int, y: int) *)
namedType       = qualifiedName [ "<" type { "," type } [ "," ] ">" ] ;   (* e.g. System.Text.StringBuilder, List<int> *)

block           = "{" { statement } [ expression ] "}" ;      (* final expression = block's value *)
statement       = variableDecl | while | for | return | try | using | defer | yield | "break" ";" | "continue" ";"
                | blockLike [ ";" ] | expression ";" | ";" ;
blockLike       = if | match | block ;                        (* ends at its '}', no ';' needed *)
variableDecl    = ( "let" | "var" ) ( IDENT [ ":" type ] | designation ) "=" expression ";" ;
designation     = "(" ( IDENT | designation ) "," ( IDENT | designation ) { "," ( IDENT | designation ) } ")" ;   (* "_" discards *)
while           = "while" expression block ;
for             = [ "await" ] "for" ( IDENT | designation ) "in" expression block ;   (* a range, anything enumerable, or with await an IAsyncEnumerable *)
try             = "try" block { catch } [ "finally" block ] ;   (* at least one catch or a finally *)
catch           = "catch" [ "(" IDENT [ ":" type ] ")" ] block ;   (* no type: any exception *)
defer           = "defer" ( block [ ";" ] | expression ";" ) ;   (* runs when the enclosing block ends *)
return          = "return" [ expression ] ";" ;
yield           = "yield" ( "return" expression | "break" ) ";" ;   (* makes the function an iterator *)
using           = [ "await" ] "using" ( "let" | "var" ) IDENT [ ":" type ] "=" expression ";"   (* disposed at the end of the block *)
                | "using" "(" [ ( "let" | "var" ) IDENT [ ":" type ] "=" ] expression ")" block ;

expression      = assignment ;
assignment      = binary [ ( "=" | "+=" | "-=" | "*=" | "/=" | "%=" ) assignment ] ;
binary          = unary { binaryOp unary | "is" pattern } ;   (* precedence table below *)
unary           = ( "-" | "!" | "await" ) unary | "(" type ")" unary | postfix ;   (* a cast, by C#'s rule; see below *)
postfix         = primary { "(" [ arguments ] ")" | "[" arguments "]" | "." IDENT [ typeArguments ] | "?" | "?." IDENT postfix } ;
                                                              (* after "?." the rest of the chain is skipped on None *)
arguments       = argument { "," argument } [ "," ] ;
argument        = expression | "out" [ "let" | "var" ] IDENT | "ref" expression ;   (* out and ref: only for .NET out and ref parameters *)
primary         = INT | DOUBLE | CHAR | STRING | "true" | "false" | "null" | IDENT [ typeArguments ] | "this"
                | "(" expression ")" | tuple | block | if | match | lambda | collection | recordSpread | "throw" expression ;
recordSpread    = "{" "..." expression { "," IDENT [ ":" expression ] } [ "," ] "}" ;   (* { ...p, y: 5, height } *)
typeArguments   = "<" type { "," type } ">" ;               (* only where C#'s rule allows; see below *)
tuple           = "(" [ IDENT ":" ] expression "," [ IDENT ":" ] expression { "," [ IDENT ":" ] expression } ")" ;
lambda          = [ "async" ] ( IDENT | "(" [ lambdaParam { "," lambdaParam } [ "," ] ] ")" ) "=>" ( block | expression ) ;
lambdaParam     = IDENT [ ":" type ] ;                        (* types may come from the expected function type *)
collection      = "[" [ element { "," element } [ "," ] ] "]" ;
element         = [ "..." ] expression ;                     (* ...xs spreads a sequence: [0, ...xs] *)
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
| 11 | postfix `()` `[]` `.` `?.` `?` | left |
| 10 | unary `-` `!` | prefix |
| 9 | `*` `/` `%` | left |
| 8 | `+` `-` | left |
| 7 | `\|>` | left |
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
matching `>` can be types and the token after it is one of `( ) ] } : ; , . ?. ? == != | && || |> [`. So
`parse<int>(s)` and `List<int>()` are generic, `a < b && c > d` is two comparisons, and `f(a < b, c > (d))` is a
generic call `a<b, c>(d)`, as in C#.

### Lexical structure

- **Identifiers:** ASCII letters, digits and `_`, not starting with a digit.
- **Keywords:** `fn let var if else while for in return break continue record enum match trait extend static this null is namespace using private try catch finally defer throw out true false`; `async`, `await`, `yield` and `ref`, which are contextual as in C# (still usable as names); and the builtin type names `int long double bool string char object byte sbyte short ushort uint ulong float decimal unit`.
- **Comments:** `// line` and `/* block */`. Block comments nest.
- **Integers:** `42`, `1_000_000`, `0xFF`, `0b1010`. Suffixes as in C#: `L` long (`3_000_000_000L`), `U` uint (or ulong if it doesn't fit), `UL` ulong. An unsuffixed literal must fit in int (C# would make a larger one a uint or long; Piper asks for the suffix); as in C#, `-2147483648` and `-9223372036854775808L` are allowed too.
- **Doubles:** `1.5`, `2e3`, `2.5E-1`. A digit is required after the `.`, so `0..10` is a range. Suffixes `F` (float), `D` (double) and `M` (decimal) work on any decimal number: `1.5f`, `19.99m`, `2m`.
- **Chars:** `'a'`, `'\n'`.
- **Strings:** `"..."`, with escapes `\n \t \r \0 \\ \" \' \{ \} \u{1F99C}`.
- **Raw strings:** as in C# 11, ```"""…"""``` (three or more quotes) has no escapes and no interpolation, so quotes and braces are literal. A multi-line raw string starts on the line after the opening quotes and ends on the line before the closing ones, whose indentation is removed from every line; line breaks are always `\n`.
- **Interpolation:** `{expression}` inside a string, for example `"sum={a + b}"`. Write `\{` for a literal brace.

## Semantics

### Types

The builtin types are C#'s, with C#'s names: `int`, `long`, `double`, `bool`, `string`, `char`, `object`, and the other numeric
types `byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong`, `float` and `decimal`. As in C# they're keywords, so they can't name
anything else, and .NET's names for them (`String`, `Int32` with `using System;`) are the same types. The one addition is
`unit`, the type of expressions evaluated only for their effect, which is written as C# `void`.
- **Records:** `record Point(x: int, y: int);` declares an immutable value type with value equality. Construct one with `Point(1, 2)` and read fields with `p.x`. To change fields, copy with a spread, as in TypeScript: `{ ...p, y: 5 }` is `p` with `y` replaced, and a field written alone takes the variable of that name (`{ ...p, height }`).
- **Implicit conversions:** C#'s implicit numeric conversions, which never lose magnitude: int → long → float → double, byte → int, char → int, int → decimal and so on. Narrowing needs a cast. As in C#, a constant that fits converts implicitly to a smaller integer type: `let b: byte = 200;`, `stream.WriteByte(7)`.
- **Arithmetic:** C#'s numeric promotion: mixed operands are converted to the wider type, so `1 + 2.5` is a double, and anything smaller than int is done as int, so `byte + byte` is an int and `'a' + 1` is 98. decimal doesn't mix with float or double, nor ulong with signed types, without a cast. `/` on integers truncates. `b += 1` on a byte works, as in C#.
- **Casts:** `(int)2.7` is 2, `(byte)300` wraps to 44 (unless it's a constant, when it's an error), `(char)65` is `'A'`, as in C#. Casts also go from a trait, `object` or a .NET base type to something more specific (throwing `InvalidCastException` if the value isn't one). As in C#, `(T)` is a cast only if the token after it can start an operand (an identifier, literal, `(`, `!`, …), so `(a) - b` is a subtraction.
- **Never:** the internal type of a block that always returns, breaks or continues. It fits wherever any type is expected, so `let x = if ok { 1 } else { return; };` type-checks.
- **Overflow:** integer arithmetic wraps around on overflow, as in C#'s default (unchecked) context: with `let max = int.MaxValue;`, `max + 1 == int.MinValue`. Constant expressions are checked, as in C#: `int.MaxValue + 1`, `(byte)300` and `1 / 0` are compile errors.
- **Tuples and arrays:** `(int, string)` is a tuple and `int[]` an array, both as in C#; see [Tuples](#tuples-and-deconstruction) and [Collections](#collections).

### Enums

An enum is a sum type: each value is exactly one of its variants, and a variant may carry fields.

```piper
enum Shape {
    Circle(radius: double),
    Rect(width: double, height: double),
    Empty,
}

let c = Shape.Circle(2.0);   // variants with fields are called like functions
let e = Shape.Empty;         // fieldless variants are written without parentheses
```

- **Variants are not types.** `Shape.Circle(2.0)` has type `Shape`.
- **Enums can be recursive**, e.g. `enum Expr { Num(value: int), Add(left: Expr, right: Expr) }`.
- **Values compare structurally** with `==`.
- **The only way to look inside a value is `match`.** There is no field access on enum values.

### Pattern matching

`match` tests a value against each arm's pattern in order and evaluates the first arm that matches.
Like `if`, it is an expression.

```piper
fn area(s: Shape) => double = match s {
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
| `42`, `-1`, `2.5`, `'c'`, `"text"`, `true` | that literal; an int literal also matches long and double values |
| `1..10`, `'a'..='z'` | a range of integer or char values; `..` excludes the end, `..=` includes it |
| `p1 \| p2` | either pattern; alternatives can bind variables if every one binds the same names with the same types, in a match arm (`Click(x, y) \| Touch(x, y) => …`) or an `is` test (`if s is Circle(r) \| Ring(r, _) { … }`) |

Patterns nest to any depth, e.g. `Add(Num(0), x)`.

- **Exhaustiveness:** a match must handle every possible value, or it is a compile error that names a missing case, e.g. `'Shape.Rect(_, _)' is not covered`. Integer and char values are covered by literals and ranges together, so `match b { 0..=127 => …, 128..=255 => … }` on a byte needs no wildcard; strings and doubles always need one.
  - Enums, records and bool are checked exactly.
  - int, string and the other types with unlimited values need a final `_` or binding arm.
  - Guarded arms don't count towards coverage.
- **Unreachable arms:** an arm that can never match, because earlier unguarded arms cover everything it would, is a warning. That includes overlapping literals and ranges: `3` after `1..=5`. A misspelled variant name usually shows up this way: it becomes a binding that matches everything, so every later arm is unreachable.
- **Arm types:** the match's type is the arms' common type, unified as for `if`.
- **Jumps in arms:** an arm may also be `return …`, `break` or `continue`. These have type Never and jump out of the enclosing function or loop.
- **Guards:** guards are bool expressions evaluated after the pattern matches, with its bindings in scope. A guard can be any bool expression, including a block with statements, a `match`, or `?` (which returns from the function, as anywhere else). It runs only for values the pattern matched.

### Generics

Records, enums, functions and methods can take type parameters.

```piper
record Pair<A, B>(first: A, second: B);
enum Option<T> { Some(value: T), None }

fn swap<A, B>(p: Pair<A, B>) => Pair<B, A> = Pair(p.second, p.first);
```

- **Inferred type arguments:** type arguments are usually inferred from the arguments, e.g. `Pair(1, "one")` is a `Pair<int, string>`.
- **Explicit type arguments:** they can also be written, as in C#: `empty<string>()`, `identity<long>(1)`, `Pair<long, string>(1, "x")`, `Option<int>.None`, `Stack<int>.empty()`.
- **The expected type helps.** When the arguments don't settle a type argument, the expected type does: `let x: Option<int> = Option.None;` or `fn f() => Option<int> = Option.None;`. If neither settles it, you get an error asking for a type annotation.
- **Numeric widening applies:** `let p: Pair<long, double> = Pair(1, 2);` converts the arguments.
- **Bounds:** `fn largest<T: Shape>(a: T, b: T) => T` requires `T` to implement the `Shape` trait, so `a.area()` can be called. Write several bounds as `T: A + B`. Bounds are checked wherever a type is instantiated.
- **Equality on type parameters:** `==` compares by value (`Equals`), on type parameters and trait-typed values too.
- **Exhaustiveness sees through type arguments:** `match o { Some(true) => …, None => … }` on an `Option<bool>` reports `'Option.Some(false)' is not covered`.

### Traits

A trait is a named set of methods, like a Rust trait or a Swift protocol. A method without a body must be
implemented; a method with a body is a default that implementers may override.

```piper
trait Shape {
    fn area() => double;
    fn describe() => string = "area {this.area()}";
}

record Circle(radius: double);

extend Circle: Shape {
    fn area() => double = 3.14159 * this.radius * this.radius;
}
```

- **Implementing a trait:** `extend Type: Trait1, Trait2 { … }` declares that a record or enum implements traits. Every required method must be present, with exactly the trait's signature.
- **Traits as types:** a trait is also a type. A `Circle` converts implicitly to `Shape`, and calls through a `Shape` value dispatch dynamically.
- **Traits as bounds:** a type parameter bound by a trait gets static dispatch.
- **Using the expected type:** when an `if` or `match` produces different types that all implement the expected trait, the trait is used, e.g. `let s: Shape = if c { circle } else { rect };`.
- **Generic traits:** a trait can take type parameters, as `trait Convert<T> { fn convert() => T; }`, implemented as `extend Celsius: Convert<string>` and used as a bound (`T: Convert<int>`) or a type (`List<Convert<string>>`). A generic type can implement one in terms of its own type parameters: `extend Box: Convert<T>`. A type implements each generic trait once, since its methods can't be overloaded: not both `Convert<string>` and `Convert<int>`.
- **Referring to the implementing type:** as in C# (`IComparable<T>`, `INumber<TSelf>`), a trait takes the implementing type as a type parameter bounded by the trait itself, and implementers name themselves: `trait Comparable<T: Comparable<T>> { fn compareTo(other: T) => int; }` with `extend Money: Comparable<Money>`, and `fn largest<T: Comparable<T>>(items: List<T>) => T`.
- **Inheritance:** `trait Person: Named, Aged { … }` builds on other traits (generic ones too: `trait Ordered<T>: Equatable<T>`). A child trait's defaults can call its parents' methods. Implementing `Person` means implementing `Named` and `Aged` as well, in the same `extend` block or separate ones. A `Person` converts to `Named`, and a bound `T: Person` gives `T` its parents' methods. A trait can't redeclare or override an inherited method, and can't inherit methods with the same name from two parents.
- **.NET interfaces:** `extend` can implement a .NET interface too, and one can be a bound. Each method of `extend Money: IComparable<Money> { fn CompareTo(other: Money?) => int = … }` has the interface's signature as Piper sees it: a parameter or result .NET marks as possibly null is an Option, as when calling .NET. Then `wallet.Sort()` uses it, `using let c = Connection(…)` works for an `IDisposable`, and the record passes wherever .NET expects the interface (`words.Sort(ByLength())` for an `IComparer<string>`). `fn largest<T: IComparable<T>>(items: List<T>)` accepts Piper types that implement it and .NET types such as `int` and `string`, and calls `a.CompareTo(b)` on them. Only interfaces made of instance methods (no properties, events, static or generic methods, or ref and out parameters) can be implemented.
- **Static methods:** `static fn empty() => T;` in a trait is a C# static abstract member, and one with a body a default (`static virtual`). Implementers write `static fn empty() => Total = …`. They are called through a type parameter bounded by the trait, as in C#: `fn concat<T: Monoid<T>>(items: List<T>) => T { var result = T.empty(); … }`. A trait with static methods can only be a bound, not the type of a value, and a trait's default static method can only be called through a type parameter, not on a type that doesn't implement it itself (`Total.describe()`).
- **Builtin and .NET types can't implement traits** (`extend int: Shape`). Traits become C# interfaces, and C# has no way to make `int` or `string` implement one, even with extension members, so they couldn't satisfy a bound either. Use a record that wraps the value, or a .NET interface they already implement (`T: IComparable<T>` accepts `int`).

### Methods and `extend`

`extend` adds methods to an existing type: a record, an enum, or a builtin such as `int` or `string`.

```piper
extend int {
    fn squared() => int = this * this;
    static fn zero() => int = 0;          // a static method, called as int.zero()
}

extend Option {                           // generic types: their type parameters are in scope
    fn getOr(fallback: T) => T = match this { Some(v) => v, None => fallback };
}

let n = 7.squared();
let s = Option.Some(5).getOr(0);
```

- **Instance and static methods:** as in C#, a method is an instance method by default, with `this` referring to the receiver. Mark it `static fn` to call it on the type instead, e.g. `int.zero()`. There is no explicit receiver parameter.
- **`this` is immutable.** Using it in a static method or a top-level function is an error.
- **Generic types:** write `extend Option`, not `extend Option<int>`. Methods apply to every instantiation.
- **Name clashes are errors.** A method can't share a name with a field, a variant, a variant's field (variants inherit their enum's methods) or another method.
- **Builtin members:** on builtin types, a method can't reuse a name the .NET type already has (such as `string.Length`), because the .NET member would silently win.

### Null safety: `Option` and `T?`

Piper has no null references. A value that might be absent has the built-in type `Option<T>`, written
`T?` as in C#, so the compiler makes every use deal with the missing case.

```piper
enum Option<T> { Some(value: T), None }      // built in; Some(…) and None can be used unqualified
```

| Syntax | Meaning |
|---|---|
| `let x: int? = 5;` | implicit `Some`: a plain value converts to its optional type |
| `let y: int? = null;` / `return null;` | `null` is `None`; it only type-checks where an optional type is expected |
| `user?.name` | `Some(user.name)` if `user` has a value, else `None`. The whole chain after `?.` is skipped, and an already-optional result isn't wrapped again (`user?.email` is `string?`, not `string??`) |
| `user?.greet();` | as a statement, the call only happens when there is a value |
| `name ?? "anonymous"` | the value, or the fallback (evaluated only if needed); right-associative, so `a ?? b ?? c` works |
| `let u = find(id)?;` | the value, or **return `None` from the enclosing function** (which must return an optional) |
| `x == null`, `x != null`, `x == 5` | comparisons; `x == 5` compares with `Some(5)` |
| `if x is Some(n) && n > 3 { … n … }` | C#-style `is` patterns. Variables bound in an `if`/`while` condition (or a match guard) are in scope in the body |
| `match x { null => …, Some(n) => … }` | `null` is also a pattern for `None`; exhaustiveness checks apply |

- **Inference through `T?`:** `fn f<T>(v: T?)` can be called as `f(5)`, which infers `T = int` and wraps the argument in `Some`.
- **`is` patterns elsewhere:** outside a condition, an `is` pattern can test but not bind, e.g. `let ok = x is Some(_);`.

### Errors as values: `Result`

```piper
enum Result<T, E> { Ok(value: T), Err(error: E) }    // built in; Ok(…) and Err(…) can be used unqualified

fn compute(a: int, b: int) => Result<int, MathError> {
    let q = divide(a, b)?;      // an Err returns from compute immediately
    Ok(q * 10)
}
```

- **`?` propagates errors.** On a `Result`, `?` unwraps `Ok`, or returns the `Err` from a function that itself returns a `Result` with a compatible error type. A `Result` has no conditional access, so `divide(a, b)?.ToString()` is `?` then `.ToString()`. On an `Option`, `?.` is always conditional access; write `(f()?).name` to unwrap first.
- **Pattern matching:** `Result` values match like any enum, e.g. `Err(MathError.DivideByZero)`.
- **No redeclaring:** `Option` and `Result` are built in, so they can't be redeclared. You can extend them with methods, e.g. `extend Option { fn isNone() => bool = this is None; }`, but not make them implement traits.
- **One runtime:** they're defined once, in the `Piper.Runtime` assembly (as `Piper.Runtime.Option<T>` and `Piper.Runtime.Result<T, E>`), which every compiled Piper program and library references. So Piper libraries can pass them to each other, and C# code can use them too.

### Programs, namespaces and visibility

A program is any number of `.piper` files compiled together, e.g. `piper run src/`. As in C#, each file
may start with a file-scoped namespace and `using` directives:

```piper
namespace Geometry;          // everything in this file is in Geometry

using System.Text;           // .NET namespaces...
using Geometry.Shapes;       // ...and Piper ones alike

record Point(x: double, y: double);
fn area(s: Shape) => double = ...;
private fn square(x: double) => double = x * x;   // visible only in this file
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

let root = Math.Sqrt(16);                  // static method (int widens to double)
let max = int.MaxValue;                    // static field
let shout = "piper".ToUpper();             // instance method on a builtin type
let length = shout.Length;                 // property
let builder = StringBuilder();             // constructor: call the type, as for Piper records
builder.Append("x = ");
builder.Append(42);                        // overload chosen by argument type

let points: List<Point> = List();          // generic types take their type arguments from the expected type
points.Add(Point(1, 2));                   // and can hold Piper values
```

- **Overload resolution** follows C#, simplified:
  - An exact match beats a numeric widening (int → long beats int → double).
  - That beats a conversion to a base type or interface, which beats `object`.
  - Ties go to the normal form over a `params` expansion, then to the overload needing fewer default arguments. Anything still tied is reported as ambiguous.
- **Null at the boundary:**
  - .NET members whose results are annotated as possibly null, and `Nullable<T>` values, come back as `T?`, with `null` becoming `None`. For example, `Environment.GetEnvironmentVariable(name) ?? "unset"`.
  - Where .NET accepts null, you can pass a `T?`, and `None` becomes `null`.
  - Piper's records, enums and traits are C# classes and interfaces, so a generic .NET result that may be null is optional for them too: `people.FirstOrDefault()` on a `List<Person>` is a `Person?`.
- **`extend` on .NET types:** a non-generic .NET type can be extended like a builtin, e.g. `extend StringBuilder { fn line(s: string) { this.AppendLine(s); } }`, but not with a member name it already has.
- **Generic and extension methods:** type arguments are inferred from the arguments, including lambdas, and extension methods from imported namespaces apply, so LINQ works: `numbers.Where(n => n > 2).Select(n => n * 10).ToList()`.
- **Indexers and setters:** `list[0]`, `ages["Ada"] = 36`, `ages["Ada"] += 1` and `builder.Length = 0` use .NET indexers, arrays and property setters.
- **Constructors of builtin types** work like any other: `string('#', 3)` is C#'s `new string('#', 3)`.
- **Explicit type arguments** work for .NET too: `Dictionary<string, int>()`, `Enumerable.Empty<int>()`, `xs.Select<int, string>(…)`.
- **`out` parameters,** as in C#: `out let n` declares `n` (`out var n` declares it mutable), `out n` assigns an existing `var` of exactly the parameter's type, and `out _` discards. A variable declared in an `if` condition stays in scope after the `if`, as in C#, so guards work:
  ```piper
  if !int.TryParse(text, out let n) { return; }
  print(n * 2);
  ```
  Where .NET may write null (`out string?`), the variable is optional. `[MaybeNullWhen(false)]`, as on `Dictionary.TryGetValue`, means null only when the call returns false, so where it returned true the variable is a plain value: in the body of `if names.TryGetValue(key, out let name) { print(name.Length); }` (or a `while`, or later in an `&&` chain), and after a guard `if !names.TryGetValue(key, out let name) { return; }`. Anywhere else it is optional. Piper functions have no `out` parameters; they return several values as a [tuple](#tuples-and-deconstruction).
- **`in` parameters** take ordinary arguments.
- **`ref` parameters** take `ref x`, where x is a `var` variable of exactly the parameter's type, or an array element: `Interlocked.Increment(ref counter)`, `Array.Resize(ref items, 10)`. .NET's nullability on a ref parameter is ignored, since the variable already has a type.
- **Events:** `list.CollectionChanged += handler` adds a handler and `-=` removes it, as in C#. The handler is a lambda or a function, converted to the event's delegate type. An event can't be read or assigned with `=`.

### Lambdas and function values

A function type `(int, int) => bool` is the .NET delegate `Func<int, int, bool>`, and one returning unit is an
`Action`. They pass straight to .NET APIs, and other delegate types (`Comparison<T>`, `Predicate<T>`) are accepted where expected.

```piper
let increment: (int) => int = x => x + 1;     // parameter types come from the expected type
let add = (a: int, b: int) => a + b;          // or from annotations: add is (int, int) => int
let total = apply(double, 21);                // a top-level function is a value too
let twice: (int) => int = identity;           // a generic one takes its type arguments from the expected type
let echo = identity<string>;                  // or from explicit ones
names.Sort((a, b) => a.Length - b.Length);    // a .NET delegate type
```

- A lambda's return type is the expected one, or else the common type of its body's value and its `return`s.
- Lambdas capture variables by reference, as in C#, so `n => { total += n; }` updates `total`.
- A lambda without annotations needs an expected function type; a lambda with too few or many parameters for it is an error.

### Collections

- **Literals:** `[1, 2, 3]` is a `List<int>`. The element type comes from the expected type (`List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, … or a .NET array) or else the elements' common type. `[]` needs an expected type.
- **Spread:** `[0, ...xs, 4]` puts every item of a sequence (a list, array, string, LINQ result or iterator: anything that is an `IEnumerable<T>`) into the literal, in order, as C#'s `[0, ..xs, 4]`. Its items count towards the common element type, and widen as C# allows (`let ys: long[] = [...ints];`), but not to an optional or a tuple of other element types.
- **`for x in collection`** works on anything implementing `IEnumerable<T>`: lists, arrays, strings (as `char`s), dictionaries and LINQ results.
- **Indexing:** `xs[i]` on arrays and on any .NET type with an indexer.
- **Arrays:** `int[]` is an array of int and `int[,]` a two-dimensional one, as in C#. `let xs: int[] = [1, 2, 3];` makes one from a literal, and `int[5]` or `double[2, 3]` creates one of default values, like C#'s `new int[5]`. That needs an element type whose default isn't null (numbers, bool, char, optionals, .NET structs and tuples of those): `string[3]` is an error, as its elements would be null, but `string?[3]` (three `None`s) is fine.

### Tuples and deconstruction

Tuples are C#'s value tuples. Elements can be named, and are read by name or as `Item1`, `Item2`, …. Tuples from .NET keep their element names, so `Math.DivRem(20, 6).Quotient` works.

```piper
fn divide(a: int, b: int) => (quotient: int, remainder: int) = (a / b, a % b);

let result = divide(17, 5);
print("{result.quotient} r {result.remainder}");
let (q, r) = divide(7, 2);                    // deconstruction: tuples, records, .NET types with Deconstruct
let (x, (_, z)) = (1, (2, 3));                // nested; _ discards
(a, b) = (b, a);                              // into existing vars: the right side is evaluated first
for (name, age) in ages { … }                 // a Dictionary's KeyValuePairs deconstruct
match cell { (0, 0) => "origin", (0, _) | (_, 0) => "axis", _ => "elsewhere" }
```

- **Names** are inferred from variables and members, as in C#: `(name, p.x)` has elements `name` and `x`. They're part of the type but not of its identity: `(x: int, y: int)` and `(int, int)` convert to each other.
- **Conversions** go element by element: `let t: (long, int?) = (1, 2);`.
- **Equality:** `==` compares element by element, for elements that support `==`.
- **Records** deconstruct into their fields, in order: `let (x, y) = point;`.
- **Patterns:** `(p1, p2)` matches a tuple, and exhaustiveness checks it like a record: `match (a, b) { (true, true) => …, (true, false) | (false, true) => …, (false, false) => … }` is exhaustive.
- A tuple has at least two elements; .NET methods taking or returning tuples work up to seven.

### Async and await

As in C#: an `async fn` returns `Task<T>`, `Task`, `ValueTask<T>` or `ValueTask`, and its body produces the `T` (or
nothing), awaiting as it goes.

```piper
using System.Net.Http;
using System.Threading.Tasks;

async fn pageLength(client: HttpClient, url: string) => Task<int> {
    let body = await client.GetStringAsync(url);
    body.Length
}

async fn main() {                              // no return type: a plain Task
    using let client = HttpClient();
    let lengths = await Task.WhenAll(["https://a.example", "https://b.example"].Select(async url => await pageLength(client, url)));
    print(lengths.Sum());
}
```

- **`await x`** works on anything awaitable by C#'s rules: a Task, a ValueTask, `ConfigureAwait(false)`, or any type with a `GetAwaiter()`. Its value is the result (`int` for a `Task<int>`; nothing for a plain `Task`). It can appear anywhere in an expression: `if await ready() { … }`, `"{await name()}"`.
- **Async lambdas:** `async x => await f(x)` and `async () => { … }`, as values of `(int) => Task<int>` and the like, or for .NET APIs such as `Task.Run` and LINQ.
- **`async fn main()`** may be the entry point.
- **`await for x in stream { … }`** iterates an `IAsyncEnumerable<T>` (C#'s `await foreach`), and **`await using`** disposes an `IAsyncDisposable` with `DisposeAsync`.
- `await` only works in an async function or lambda. An async function with no `await` gets a warning, as in C#, since it runs synchronously. `async` and `await` are contextual keywords, as in C#, so they can still be names.
- A trait's required methods can't be `async` (it's part of a body, not a signature), but an implementation can be.

### Iterators: `yield`

As in C#, a function whose body contains `yield` is an iterator: it returns `IEnumerable<T>` (or `IEnumerator<T>`, or with
`async` `IAsyncEnumerable<T>`), produces values with `yield return` and stops with `yield break` or at its end. It runs
lazily, a step at a time as the caller iterates.

```piper
fn fibonacci() => IEnumerable<long> {
    var (a, b) = (0L, 1L);
    while true {                              // endless: callers take what they need
        yield return a;
        (a, b) = (b, a + b);
    }
}

print(string.Join(", ", fibonacci().Take(10)));

async fn ticks(count: int) => IAsyncEnumerable<int> {
    for i in 1..=count {
        await Task.Delay(100);
        yield return i;
    }
}

await for tick in ticks(3) { print(tick); }
```

- An iterator can't `return`; `yield break` ends it early.
- As in C#, `yield return` can't be inside a `try` that has a `catch`, and `yield` can't be in a `catch` or `finally`; a `try`/`finally` around `yield return` is fine (the `finally` runs when the caller stops iterating).
- Lambdas can't be iterators.

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
- `break`, `continue`, `return` and `?` can't leave a `finally` block.

### Disposing resources: `using`

As in C#, `using` disposes a value that implements `System.IDisposable` when it goes out of scope.

```piper
fn countLines(path: string) => int {
    using let reader = File.OpenText(path);   // disposed when the function returns, by any path
    var count = 0;
    while reader.ReadLine() is Some(_) {
        count += 1;
    }
    count
}

using (let writer = StringWriter()) {         // disposed after the block
    writer.Write("hello");
}
```

- A `using let` (or `using var`) variable is disposed at the end of the block that declares it, even when that block is an expression, and can't be reassigned.
- `using (expression) { … }` disposes the value of any expression after the block.

### Cleaning up: `defer`

`defer` runs code when the enclosing block ends, however it ends: by reaching its end, `return`, `break`, `continue`, `?`
or an exception. It is Swift's `defer`, and C#'s `try`/`finally` without the nesting.

```piper
fn copy(from: string, to: string) {
    let input = File.OpenRead(from);
    defer input.Close();
    let output = File.Create(to);
    defer { output.Flush(); output.Close(); }
    input.CopyTo(output);
}                                             // output is closed, then input
```

- The deferred code is a single expression followed by `;`, or a block.
- Several defers in a block run in reverse order, last first.
- It runs at the end of the block it is written in, not the function: a `defer` in a loop body runs at the end of every iteration. A block with a value computes the value first, then runs its defers.
- It sees the variables declared before it, but not after.
- Like a `finally` block, it can't leave: no `return`, `?`, `yield`, or `break`/`continue` out of an enclosing loop.

### Bindings

- `let` declares an immutable binding and `var` a mutable one. Assigning to a `let` binding, a parameter or a `for` loop variable is a compile error.
- Shadowing is allowed in nested scopes (`let x = x + 1;` inside a block), but redeclaring a name in the same scope is not.
- Declarations at the top level can appear in any order.

### Expressions everywhere

- **Blocks are expressions.** A block's value is its final expression, written without a `;`: `let y = { let t = 2; t * 3 };`.
- **`if` is an expression.** `let s = if c { "a" } else { "b" };` gives both branches' common type. An `if` without `else` has type unit.
- **Function bodies follow the same rule.** A function body's final expression is its return value. A function with a return type must end in a value or return on every path; as in C#, a `while true` loop with no `break` never ends, so a function may end with one.
- **Assignment is a statement-like expression of type unit,** so `a = b = c` is rejected.
- **Conditions must be `bool`.** There is no truthiness.

### The pipe operator: `|>`

`x |> f(a, b)` is `f(x, a, b)`: the value on the left becomes the first argument of the call on the right, and
`x |> f` is `f(x)`. A chain of calls then reads in the order it runs.

```piper
let total = orders
    |> Enumerable.Where(o => o.paid)
    |> Enumerable.Sum(o => o.amount)
    |> round(2);
```

- The right side is anything that can be called: a function, a function value, a record, or a .NET method (`|> Math.Abs`, `|> Enumerable.ToList()`).
- It binds looser than arithmetic and tighter than comparison, so `a + b |> f == c` is `f(a + b) == c`. Chains go left to right.
- It is plain call syntax, so it emits an ordinary C# call.

### Evaluation order

Evaluation is strictly left to right, including operands that contain blocks.
- `x += e` reads `x` before evaluating `e`.
- `x |> f(a)` evaluates `f`, then `x`, then `a`, as the call `f(x, a)` would.
- The right-hand side of `&&` or `||` is evaluated only when needed.
- The upper bound of `for i in a..b` is evaluated once.

### Builtins

`print(value)` writes any value followed by a newline, using its C# `ToString`: Bools print as `True`/`False`,
records as `Point { x = 1, y = 2 }`, fieldless enum variants by name (`Food`, as a C# enum member) and tuples as `(1, a)`. Output
is locale-independent. `main()` takes no parameters and returns unit; it is the program's entry point.

## How Piper maps to C#

| Piper | C# |
|---|---|
| `record P(x: int)` | `public sealed record P(int x);` |
| `{ ...p, y: 5 }` | `p with { y = 5 }` |
| `[0, ...xs]` | `(List<int>)[0, .. xs]` |
| `enum Shape { Circle(r: double), Empty }` | `public abstract record Shape` with a private constructor and nested `public sealed record Circle(double r) : Shape;`, `public sealed record Empty() : Shape;` |
| `Shape.Circle(1.0)` | `new Shape.Circle(1.0)` |
| `match s { Circle(r) if r > 1.0 => a, Empty => b, … }` | `if (s is Shape.Circle(var r) && r > 1.0) { … } else if … else { … }` |
| patterns `1 \| 2`, `1..=9`, `_` | C# patterns `1 or 2`, `>= 1 and <= 9`, `_` |
| `record Pair<A, B: Show>(…)` | `public sealed record Pair<A, B>(…) where B : Show` |
| `swap(p)` (generic) | `swap<int, string>(p)`: type arguments are always explicit |
| `(int, string)`, `(x: int, y: int)`, `(1, "a")`, `t.x` | `(int, string)`, `(int x, int y)`, `(1, "a")`, `t.Item1` (always by position) |
| `let (a, b) = t;`, `(a, b) = (b, a);`, `for (k, v) in d` | `var (a, b) = t;`, `(a, b) = (b, a);`, `foreach (var (k, v) in d)` |
| `int[]`, `int[5]`, `double[2, 3]` | `int[]`, `new int[5]`, `new double[2, 3]` |
| `int.TryParse(s, out let n)` | `int n;` before the statement, then `int.TryParse(s, out n)`; an optional `out` goes through `PiperInterop.AfterOutReference(…)` |
| `Interlocked.Increment(ref n)`, `list.Changed += h` | the same: `ref n`, `list.Changed += h` |
| `trait Shape { fn area() => double; fn d() => string = …; }` | `public interface Shape { double area(); string d() => …; }` |
| `trait Ordered<T: Ordered<T>>: Equatable<T> { … }` | `public interface Ordered<T> : Equatable<T> where T : Ordered<T> { … }` |
| `trait Monoid<T: Monoid<T>> { static fn empty() => T; static fn d() => string = …; }`, `T.empty()` | `interface Monoid<T> where T : Monoid<T> { static abstract T empty(); static virtual string d() => …; }`, `T.empty()` |
| `extend Money: IComparable<Money> { fn CompareTo(other: Money?) => int … }` | `record Money : IComparable<Money>` with `public int CompareTo(Option<Money> other)`, plus `int IComparable<Money>.CompareTo(Money? other) => CompareTo(PiperInterop.FromNullableReference(other));` where nullability differs |
| `extend Circle: Shape { fn area() … }` | members inside `public sealed record Circle(…) : Shape { public double area() … }` |
| `c.d()` where `d` is an un-overridden default | `((Shape)c).d()` (C# only exposes default interface methods through the interface) |
| `extend int { fn squared() … static fn zero() … }` | C# 14 `extension(int self) { public int squared() … }` and `extension(int) { public static int zero() … }` |
| `this` | `this` in members; in C# extension blocks the receiver parameter is named `self`, since C# reserves `this` |
| `int?`, `Result<int, E>` | `Option<int>`, `Result<int, E>`: small `readonly record struct`s added to the output when used (not C#'s `int?`, which can't represent `None` for an unconstrained generic `T` or nest) |
| `Some(x)`, `None`, `null` | `Option<int>.Some(x)`, `Option<int>.None` |
| `match o { Some(v) => …, None => … }`, `o is Some(v)` | property patterns `o is { IsSome: true, Value: var v }`, `{ IsSome: false }` |
| `a ?? b`, `a?.b` | `a.IsSome ? a.Value : b`, `a.IsSome ? Option<T>.Some(a.Value.b) : Option<T>.None` (non-variable operands are first stored in a temporary) |
| `let x = f()?;` | `var t = f(); if (!t.IsSome) { return Option<R>.None; } x = t.Value;` |
| a `.piper` file | a `.cs` file of the same name, starting `using Piper.Runtime;` (for Option, Result and the `PiperInterop` helpers); the file with `main` also gets the entry point |
| `async fn f() => Task<int>`, `await t` | `async Task<int> f()`, `await t` |
| `async fn main()` | an `async Task main()`, run by `main().GetAwaiter().GetResult()` |
| `await for x in s`, `await using let r = …;` | `await foreach (T x in s)`, `await using T r = …;` |
| `yield return x;`, `yield break;` | the same, in an iterator method returning `IEnumerable<T>` (or `IAsyncEnumerable<T>`) |
| `extend Option { … }` | C# 14 `extension<T>(Option<T> self) { … }`, since Option lives in Piper.Runtime |
| `namespace Geometry;` | `namespace Geometry { … }`; names from other namespaces are written `global::Geometry.Point` |
| `private record R(…)` / `private trait T` | `file sealed record R(…)` / `file interface T` (C# 11 file-local types) |
| `private fn f()` / private method | `internal static` in the file's module class / `internal` member |
| `extend` of a type declared in another file | that type becomes `partial`; the other file holds a `partial record` part with the added members |
| `Math.Sqrt(2.0)`, `StringBuilder()`, `s.Length` | `global::System.Math.Sqrt(2.0)`, `new global::System.Text.StringBuilder()`, `s.Length` |
| a possibly-null .NET result / a `T?` passed to .NET | `PiperInterop.FromNullableReference(…)` / `PiperInterop.ToNullableReference(…)` (and `…Value` versions for `Nullable<T>`) |
| `fn f(a: int) => int { … }` | `public static int f(int a) { … }` in a static module class |
| `fn f(a: int) => int = e;` | `public static int f(int a) => e;` |
| `let x = e;` / `var x = e;` | `int x = e;` (immutability is checked by Piper, not C#) |
| `if` expression, simple branches | `c ? a : b` |
| `if` expression, branches with statements | a variable assigned in each branch, or `return` in each branch |
| `for i in a..b` / `a..=b` | `for (int i = a; i < b; i++)` / `<=` (non-constant bounds hoisted) |
| `for x in xs` | `foreach (int x in xs)` |
| `[1, 2]` | `new List<int> { 1, 2 }` (or `new T[] { … }` for an expected array), which needs no target type, unlike C#'s `[1, 2]` |
| `(int) => bool`, `x => x > 0` | `Func<int, bool>`, `(int x) => x > 0`: parameter types are always written |
| `f` used as a value | the method group `f` |
| `xs.Where(n => n > 2)` (extension method) | `Enumerable.Where<int>(xs, (int n) => n > 2)`: called statically, type arguments explicit |
| `try`/`catch (e: E)`/`finally`, `throw e` | `try`/`catch (E e)`/`finally`, `throw e;` (or a C# throw expression in `?:`) |
| `x \|> f(a)` | `f(x, a)` |
| `defer d; rest` | `try { rest } finally { d }`, with the block's value stored in a temporary inside the `try` |
| `"…{e}…"` | `$"…{e}…"` |
| `print(x)` | `global::System.Console.WriteLine(x)` |
| `(byte)x`, `1.5f`, `19.99m`, `string('#', 3)` | `(byte)x`, `1.5f`, `19.99m`, `new string('#', 3)` |
| `using let r = open();`, `using (let r = open()) { … }` | `using T r = open();`, `using (T r = open()) { … }` |
| `a == b` on a type parameter or trait | `EqualityComparer<T>.Default.Equals(a, b)` |
| `A(x) \| B(x) => body` | one `if` branch per alternative, each with the body (C# can't declare variables in an `or` pattern) |
| with `#line` directives (`piper run`, `piper build`, Piper.Build) | `#line 12 "hello.piper"` before each statement, so errors, stack traces and debuggers show Piper lines |

- **Locals** get unique C# names (`x`, `x_1`, …), because C# forbids shadowing.
- **Identifiers that are C# keywords** are escaped (`@class`).
- **The module class** is named after the source file (`hello.piper` → `Hello`). A `PiperEntryPoint.Main` calls `main`.

**Why `match` becomes an if/else chain, not a C# `switch`.**
- **No double-checking:** Piper checks exhaustiveness and reachability itself. C# treats a switch arm it considers subsumed as an error, and its analysis isn't identical to Piper's.
- **Jumps:** a `break` inside a C# `switch` statement would exit the switch rather than the enclosing loop.
- **Shared lowering:** the chain reuses the lowering already used for `if`, which handles arms with statements, returns and values.

The match is known to be exhaustive, so a final arm without a guard or bindings becomes a plain `else`. Otherwise the chain ends in `throw new UnreachableException()`.

A guard that needs statements (a block, `?`, or `?.`/`??` on a call) can't go in an `if` condition, so its arm becomes nested
ifs: `if (s is P(var x)) { …; if (guard) { body; goto end; } }`, followed by the later arms and an `end: ;` label. An `if` or
`while` whose condition binds variables with `is` and then needs statements is lowered the same way, so C# still knows the
variables are assigned.

## Known limitations

- Builtin and .NET types can't implement traits, because C# can't make them implement an interface (see [Traits](#traits)). A trait can't override a method it inherits.
- Trait methods can't have type parameters of their own, and a trait can't be the target of `extend` (add default methods to the trait instead).

## Roadmap

1. ~~**Sum types and `match`**~~, done in milestone 2.
2. ~~**Null safety and errors**~~, done in milestone 4: `Option`/`T?`, `Result`, `null`, `?.`, `??`, `?` and `is` patterns.
3. ~~**Traits, generics and extensions**~~, done in milestone 3.
4. ~~**Modules, imports and .NET interop**~~, done in milestone 5: `namespace`, `using`, `private`, multi-file programs, and calling .NET.
5. ~~**Collections and lambdas**~~, done in milestone 6: lambdas and function types, LINQ, collection literals, `for x in collection`, indexing, property setters, and `try`/`catch`/`throw`.
6. ~~**Language freeze**~~, done in milestone 7: C#-compatible printing and minimum-value literals, arrays (`int[]`, `int[5]`), explicit type arguments, generic functions as values, tuples and deconstruction, and `out` parameters.
7. ~~**Robustness and tooling**~~, done in milestone 8: `#line` mapping to `.piper` sources, constant checking and C# errors reported as internal compiler errors, larger sample programs, raw strings, C#'s numeric types and casts, `using`, equality on type parameters, `|` patterns that bind, the `piper` dotnet tool and the Piper.Build MSBuild package.
8. ~~**Async and iterators**~~, done after milestone 8: `async`/`await`, async lambdas and `main`, `await for`, `await using`, `yield` iterators (sync and async), and the shared `Piper.Runtime` assembly.
9. ~~**Smaller features**~~, done: spreading sequences into collection literals (`[0, ...xs]`), `defer` and the pipe operator `|>`.
10. **A language server** that reuses the fault-tolerant parser and binder.
