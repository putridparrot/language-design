# The Piper language (milestone 8)

Piper is a small, C-style, expression-oriented language that transpiles to C#. It borrows
immutable-by-default bindings, block expressions, enums (sum types), exhaustive `match` and traits
from Rust, `let` and retroactive `extend` from Swift, `mut` from Rust, and records, generics, string
interpolation and the `T?` / `?.` / `??` null-handling syntax from C#. There are no null references:
absence is the built-in `Option<T>`, and errors are values of the built-in `Result<T, E>`. Generated code needs C# 14 (.NET 10), which is used for extension members.

```piper
record Point(x: int, y: int);

fn Add(a: int, b: int) => int = a + b;

fn Classify(n: int) => string {
    if n < 0 { "negative" } else if n == 0 { "zero" } else { "positive" }
}

fn Main() {
    let p = Point(1, 2);
    mut total = 0;
    for i in 0..<10 { total += i; }
    Print("p=({p.x}, {p.y}) total={total} {Classify(-3)}");
}
```

## Reference grammar

This grammar is **documentation**, not the source of truth: the hand-written parser in
[Parser.cs](../src/Piper.Compiler/Syntax/Parser.cs) is, and each `Parse*` method carries the rule
it implements. Keep the two in step when changing syntax.

Notation: `{ x }` is zero or more, `[ x ]` is optional, `|` separates alternatives, `"x"` is a literal token.

```ebnf
compilationUnit = { using } [ "namespace" qualifiedName ";" ] { using } { attributes [ "private" ] declaration } EOF ;
using           = "using" qualifiedName ";" ;                (* a Piper or .NET namespace *)
qualifiedName   = IDENT { "." IDENT } ;
declaration     = function | record | class | enum | trait | extend | test | pluginDeclaration ;
pluginDeclaration = KEYWORD … ;                               (* a plugin's syntax: see plugins.md *)
test            = [ "async" ] "test" STRING block ;            (* run by piper test, left out of other builds *)
attributes      = { "[" attribute { "," attribute } [ "," ] "]" } ;
attribute       = qualifiedName [ "(" [ expression { "," expression } [ "," ] ] ")" ] ;   (* constants, then Name = value *)

function        = [ "static" ] [ "async" ] ( "fn" IDENT [ typeParameters ] | "operator" ( "+" | "-" | "*" | "/" | "%" ) ) "(" [ parameters ] ")" [ "=>" type ]
                  { requires } ( block | "=" expression ";" | ";" ) ;      (* ";" only for required trait methods; "static" only in traits and extend blocks *)
record          = "record" IDENT [ typeParameters ] "(" [ parameters ] ")" { requires } ";" ;
class           = [ "abstract" | "sealed" ] "class" IDENT [ typeParameters ] [ "(" [ parameters ] ")" ]
                  [ ":" baseType { "," baseType } ] "{" { classMember } "}" ;
baseType        = type [ "(" [ arguments ] ")" ] ;               (* a base class first, with its constructor arguments; then traits and interfaces *)
classMember     = attributes [ "public" | "private" ] ( field | [ "static" ] [ "virtual" | "override" | "abstract" ] function ) ;
field           = ( "let" | "mut" ) IDENT [ ":" type ] "=" expression ";" ;   (* private unless "public"; methods are public unless "private" *)
requires        = "requires" condition { "," condition } ;    (* condition: an expression without assignment, of type bool *)
enum            = "enum" IDENT [ typeParameters ] "{" [ variant { "," variant } [ "," ] ] "}" ;
variant         = IDENT [ "(" [ parameters ] ")" ] ;
trait           = "trait" IDENT [ typeParameters ] [ ":" type { "," type } ] "{" { attributes function } "}" ;   (* ": Named" inherits *)
extend          = "extend" namedType [ ":" type { "," type } ] "{" { attributes [ "private" ] function } "}" ;
typeParameters  = "<" typeParameter { "," typeParameter } [ "," ] ">" ;
typeParameter   = IDENT [ ":" type { "+" type } ] ;           (* bounds must be traits *)
parameters      = parameter { "," parameter } [ "," ] ;
parameter       = attributes IDENT ":" type [ "=" expression ] ;   (* a default: a constant, or null for an optional *)
type            = ( namedType | functionType | tupleType ) { "?" | "[" { "," } "]" } ;   (* T? is Option<T>; int[] an array, int[,] 2-D *)
functionType    = "(" [ type { "," type } [ "," ] ] ")" "=>" type ;   (* (int) => bool is Func<int, bool> *)
tupleType       = "(" tupleElement "," tupleElement { "," tupleElement } ")" ;
tupleElement    = [ IDENT ":" ] type ;                        (* (x: int, y: int) *)
namedType       = qualifiedName [ "<" type { "," type } [ "," ] ">" ] ;   (* e.g. System.Text.StringBuilder, List<int> *)

block           = "{" { statement } [ expression ] "}" ;      (* final expression = block's value *)
statement       = variableDecl | while | for | return | try | using | defer | yield | "break" ";" | "continue" ";"
                | blockLike [ ";" ] | expression ";" | ";" | pluginStatement | localFunction ;
localFunction   = function ;                                  (* indented: an `fn` starting a line is taken to be the next top-level function *)
blockLike       = if | match | block | (* an expression ending with a trailingClosure *) ;   (* ends at its '}', no ';' needed *)
pluginStatement = KEYWORD … ;                                 (* a plugin's syntax: see plugins.md *)
variableDecl    = ( "let" | "mut" ) ( IDENT [ ":" type ] | designation ) "=" expression ";"
                | "let" pattern "=" expression "else" block [ ";" ] ;   (* let ... else: the pattern starts with "[" or a name followed by "(", "." or "{" *)
designation     = "(" ( IDENT | designation ) "," ( IDENT | designation ) { "," ( IDENT | designation ) } ")" ;   (* "_" discards *)
while           = "while" expression block ;
for             = [ "await" ] "for" ( IDENT | designation ) "in" expression block ;   (* a range, anything enumerable, or with await an IAsyncEnumerable *)
try             = "try" block { catch } [ "finally" block ] ;   (* at least one catch or a finally *)
catch           = "catch" [ "(" IDENT [ ":" type ] ")" ] block ;   (* no type: any exception *)
defer           = "defer" ( block [ ";" ] | expression ";" ) ;   (* runs when the enclosing block ends *)
return          = "return" [ expression ] ";" ;
yield           = "yield" ( "return" expression | "break" ) ";" ;   (* makes the function an iterator *)
using           = [ "await" ] "using" ( "let" | "mut" ) IDENT [ ":" type ] "=" expression ";"   (* disposed at the end of the block *)
                | "using" "(" [ ( "let" | "mut" ) IDENT [ ":" type ] "=" ] expression ")" block ;

expression      = assignment ;
assignment      = binary [ ( "=" | "+=" | "-=" | "*=" | "/=" | "%=" ) assignment ] ;
binary          = unary { binaryOp unary | "is" pattern } ;   (* precedence table below *)
unary           = ( "-" | "!" | "await" ) unary | "(" type ")" unary | postfix ;   (* a cast, by C#'s rule; see below *)
postfix         = primary { "(" [ arguments ] ")" [ trailingClosure ] | "[" arguments "]" | "." IDENT [ typeArguments ] | "?" | "?." IDENT postfix }
                | ( IDENT | postfix "." IDENT ) trailingClosure ;
                                                              (* after "?." the rest of the chain is skipped on None *)
trailingClosure = "{" [ ( IDENT | "(" [ lambdaParam { "," lambdaParam } [ "," ] ] ")" ) "=>" ] { statement } [ expression ] "}" ;
                                                              (* the call's last argument; not in an if/while/for/match/requires head *)
arguments       = argument { "," argument } [ "," ] ;
argument        = [ IDENT ":" ] ( expression | "out" [ "let" | "mut" ] IDENT | "ref" expression ) ;   (* name: a named argument; out and ref: only for .NET out and ref parameters *)
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
                | [ [ IDENT "." ] IDENT ] properties                      (* property pattern *)
                | "[" [ listElement { "," listElement } [ "," ] ] "]"     (* list pattern *)
                | ( "<" | "<=" | ">" | ">=" ) literalPattern              (* relational pattern *)
                | literalPattern [ ( ".." | "..<" ) literalPattern ] ;
properties      = "{" [ IDENT ":" pattern { "," IDENT ":" pattern } [ "," ] ] "}" ;
listElement     = pattern | "..." [ IDENT ] ;
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
| 1 | `..` `..<` | left (only valid in `for`) |
| 0 | `=` `+=` `-=` `*=` `/=` `%=` | right |

The table lives in [SyntaxFacts.cs](../src/Piper.Compiler/Syntax/SyntaxFacts.cs); changing a
number there is all it takes to change precedence.

**Type arguments or less-than?** As in C#, a `<` after a name starts type arguments only if the tokens up to the
matching `>` can be types and the token after it is one of `( ) ] } : ; , . ?. ? == != | && || |> [`. So
`parse<int>(s)` and `List<int>()` are generic, `a < b && c > d` is two comparisons, and `f(a < b, c > (d))` is a
generic call `a<b, c>(d)`, as in C#.

### Lexical structure

- **Identifiers:** ASCII letters, digits and `_`, not starting with a digit.
- **Reserved words:** `requires` is a keyword only after a signature, but can't be a name anywhere. `const operator test where type impl module import self public internal protected sealed abstract override virtual class struct interface` aren't keywords yet but may become ones, so they can't be used as names (of variables, parameters, fields, functions or types). `class`, `abstract`, `sealed`, `virtual` and `override` are used by [classes](#classes) and still can't be names. They can still follow a dot, to reach a .NET member of that name.
- **Keywords:** `fn let mut if else while for in return break continue record enum match trait extend static this null is namespace using private try catch finally defer throw out true false`; `async`, `await`, `yield` and `ref`, which are contextual as in C# (still usable as names); and the builtin type names `int long double bool string char object byte sbyte short ushort uint ulong float decimal unit`.
- **Comments:** `// line` and `/* block */`. Block comments nest. `///` lines directly above a declaration, method, parameter, field or enum variant are its documentation: the editor shows it, and the generated C# carries it as XML documentation (`piper build --exe` and `--library` also write the `.xml` file).
- **Integers:** `42`, `1_000_000`, `0xFF`, `0b1010`. Suffixes as in C#: `L` long (`3_000_000_000L`), `U` uint (or ulong if it doesn't fit), `UL` ulong. An unsuffixed literal must fit in int (C# would make a larger one a uint or long; Piper asks for the suffix); as in C#, `-2147483648` and `-9223372036854775808L` are allowed too.
- **Doubles:** `1.5`, `2e3`, `2.5E-1`. A digit is required after the `.`, so `0..10` is a range. Suffixes `F` (float), `D` (double) and `M` (decimal) work on any decimal number: `1.5f`, `19.99m`, `2m`.
- **Chars:** `'a'`, `'\n'`.
- **Strings:** `"..."`, with escapes `\n \t \r \0 \\ \" \' \{ \} \u{1F99C}`.
- **Raw strings:** as in C# 11, ```"""…"""``` (three or more quotes) has no escapes and no interpolation, so quotes and braces are literal. A multi-line raw string starts on the line after the opening quotes and ends on the line before the closing ones, whose indentation is removed from every line; line breaks are always `\n`.
- **Interpolation:** `{expression}` inside a string, for example `"sum={a + b}"`. Write `\{` for a literal brace. As in C#, an alignment and a format can follow the expression: `"{price:C}"`, `"{date:yyyy-MM-dd}"`, `"{name,-10}"`, `"{total,8:F2}"`. A `,` or `:` inside brackets (a named tuple element, a lambda's parameter type) is part of the expression.

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
fn Area(s: Shape) => double = match s {
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
| `Point(x, y)`, `Message.Add(n)` on an `object`, trait or interface | a type test, as C#'s `o is Point(var x, var y)`: a value of that record or variant (of a non-generic type that the matched type can hold), deconstructed. A match on such a value needs a `_` arm |
| `42`, `-1`, `2.5`, `'c'`, `"text"`, `true` | that literal; an int literal also matches long and double values |
| `1..10`, `0..<10`, `'a'..'z'` | a range of integer or char values; `..` includes the end, as in Swift, and `..<` stops before it |
| `p1 \| p2` | either pattern; alternatives can bind variables if every one binds the same names with the same types, in a match arm (`Click(x, y) \| Touch(x, y) => …`) or an `is` test (`if s is Circle(r) \| Ring(r, _) { … }`) |
| `< 0`, `>= 90.0`, `<= 'z'` | a number or char compared with a constant |
| `Point { x: 0 }`, `Circle { radius: r }`, `{ y: 0 }` | a record or variant by field name; fields it doesn't name match anything. Without a type name it matches the value's own type |
| `{ Length: > 5 }` | a .NET value by its properties and fields |
| `[]`, `[x]`, `[first, ...rest]`, `[_, ..., last]` | an array, string, `List` or other .NET collection by its elements; `...` matches any number of them, and `...rest` binds them |

Patterns nest to any depth, e.g. `Add(Num(0), x)` or `[{ x: 0 }, ...]`.

- **List patterns** work on arrays, strings, `List<T>` and any .NET type with a `Count` (or `Length`) and an `int` indexer, as in C#. `...rest` binds the elements between the patterns before and after it, as a value of the matched type (an array, string or `List`); other collections can only use `...` alone. A list pattern has at most one `...`.
- **Property patterns** on a record or variant are another way of writing a constructor pattern: `Point { x: 0 }` is `Point(0, _)`, so they count towards exhaustiveness the same way. On a .NET type, the names are its properties and fields, as you'd read them (`text is { Length: 0 }`); a member that can be null, which Piper reads as an optional, can't be matched this way. `{}` matches any value, since Piper values aren't null.
- **Relational patterns** compare with a literal of the matched type, as C#'s do. On integers and chars they're ranges as far as exhaustiveness goes: `< 0`, `0` and `> 0` cover every `int`, and `< 128` with `128..255` every `byte`.

- **Exhaustiveness:** a match must handle every possible value, or it is a compile error that names a missing case, e.g. `'Shape.Rect(_, _)' is not covered` or, for a list, `'[_, _, ...]' is not covered`. Integer and char values are covered by literals and ranges together, so `match b { 0..127 => …, 128..255 => … }` on a byte needs no wildcard; strings and doubles always need one.
  - Enums, records and bool are checked exactly.
  - int, string and the other types with unlimited values need a final `_` or binding arm.
  - Guarded arms don't count towards coverage.
- **Unreachable arms:** an arm that can never match, because earlier unguarded arms cover everything it would, is a warning. That includes overlapping literals and ranges: `3` after `1..5`. A misspelled variant name usually shows up this way: it becomes a binding that matches everything, so every later arm is unreachable.
- **Arm types:** the match's type is the arms' common type, unified as for `if`.
- **Jumps in arms:** an arm may also be `return …`, `break` or `continue`. These have type Never and jump out of the enclosing function or loop.
- **Guards:** guards are bool expressions evaluated after the pattern matches, with its bindings in scope. A guard can be any bool expression, including a block with statements, a `match`, or `?` (which returns from the function, as anywhere else). It runs only for values the pattern matched.

### Generics

Records, enums, functions and methods can take type parameters.

```piper
record Pair<A, B>(first: A, second: B);
enum Option<T> { Some(value: T), None }

fn Swap<A, B>(p: Pair<A, B>) => Pair<B, A> = Pair(p.second, p.first);
```

- **Inferred type arguments:** type arguments are usually inferred from the arguments, e.g. `Pair(1, "one")` is a `Pair<int, string>`.
- **Explicit type arguments:** they can also be written, as in C#: `Empty<string>()`, `Identity<long>(1)`, `Pair<long, string>(1, "x")`, `Option<int>.None`, `Stack<int>.Empty()`.
- **The expected type helps.** When the arguments don't settle a type argument, the expected type does: `let x: Option<int> = Option.None;` or `fn F() => Option<int> = Option.None;`. If neither settles it, you get an error asking for a type annotation.
- **Numeric widening applies:** `let p: Pair<long, double> = Pair(1, 2);` converts the arguments.
- **Bounds:** `fn Largest<T: Shape>(a: T, b: T) => T` requires `T` to implement the `Shape` trait, so `a.Area()` can be called. Write several bounds as `T: A + B`. Bounds are checked wherever a type is instantiated.
- **Equality on type parameters:** `==` compares by value (`Equals`), on type parameters and trait-typed values too.
- **Exhaustiveness sees through type arguments:** `match o { Some(true) => …, None => … }` on an `Option<bool>` reports `'Option.Some(false)' is not covered`.

### Traits

A trait is a named set of methods, like a Rust trait or a Swift protocol. A method without a body must be
implemented; a method with a body is a default that implementers may override.

```piper
trait Shape {
    fn Area() => double;
    fn Describe() => string = "area {this.Area()}";
}

record Circle(radius: double);

extend Circle: Shape {
    fn Area() => double = 3.14159 * this.radius * this.radius;
}
```

- **Implementing a trait:** `extend Type: Trait1, Trait2 { … }` declares that a record or enum implements traits. Every required method must be present, with exactly the trait's signature.
- **Traits as types:** a trait is also a type. A `Circle` converts implicitly to `Shape`, and calls through a `Shape` value dispatch dynamically.
- **Traits as bounds:** a type parameter bound by a trait gets static dispatch.
- **Using the expected type:** when an `if` or `match` produces different types that all implement the expected trait, the trait is used, e.g. `let s: Shape = if c { circle } else { rect };`.
- **Generic traits:** a trait can take type parameters, as `trait Convert<T> { fn Convert() => T; }`, implemented as `extend Celsius: Convert<string>` and used as a bound (`T: Convert<int>`) or a type (`List<Convert<string>>`). A generic type can implement one in terms of its own type parameters: `extend Box: Convert<T>`. A type implements each generic trait once, since its methods can't be overloaded: not both `Convert<string>` and `Convert<int>`.
- **Referring to the implementing type:** as in C# (`IComparable<T>`, `INumber<TSelf>`), a trait takes the implementing type as a type parameter bounded by the trait itself, and implementers name themselves: `trait Comparable<T: Comparable<T>> { fn CompareTo(other: T) => int; }` with `extend Money: Comparable<Money>`, and `fn Largest<T: Comparable<T>>(items: List<T>) => T`.
- **Inheritance:** `trait Person: Named, Aged { … }` builds on other traits (generic ones too: `trait Ordered<T>: Equatable<T>`). A child trait's defaults can call its parents' methods. Implementing `Person` means implementing `Named` and `Aged` as well, in the same `extend` block or separate ones. A `Person` converts to `Named`, and a bound `T: Person` gives `T` its parents' methods.
  - **Overriding:** a child trait can give an inherited method a new default by declaring it with a body and the same signature: `trait Formal: Named { fn Greet() => string = "Good day, {this.Name()}"; }`. A required method gets a default the same way. Calls through `Named` use the most specific default, so a `Judge` that implements `Formal` greets formally wherever it is used. Static methods can't be overridden, and declaring an inherited method without a body is an error.
  - **Ambiguity:** two parents can't both declare a method with the same name. If two unrelated parents override the same method, a trait or type that inherits both must override it again, as in C#.
- **Generic methods:** a trait method can take type parameters of its own, `fn MapAll<U>(f: (T) => U) => List<U>;`. An implementation has as many type parameters, with the same bounds, under any names: `fn MapAll<R>(f: (int) => R) => List<R>`.
- **Adding methods to a trait:** `extend Named { fn Shout() => string = this.Greet().ToUpper(); }` adds default methods to a trait, including one declared in another file or namespace, and every implementer gets them. They must have bodies and can't be static or private, and the block can't make the trait implement other traits (list parents in its declaration).
- **.NET interfaces:** `extend` can implement a .NET interface too, and one can be a bound. Each method of `extend Money: IComparable<Money> { fn CompareTo(other: Money?) => int = … }` has the interface's signature as Piper sees it: a parameter or result .NET marks as possibly null is an Option, as when calling .NET. Then `wallet.Sort()` uses it, `using let c = Connection(…)` works for an `IDisposable`, and the record passes wherever .NET expects the interface (`words.Sort(ByLength())` for an `IComparer<string>`). `fn Largest<T: IComparable<T>>(items: List<T>)` accepts Piper types that implement it and .NET types such as `int` and `string`, and calls `a.CompareTo(b)` on them. Only interfaces made of instance methods (no properties, events, static or generic methods, or ref and out parameters) can be implemented.
- **Static methods:** `static fn Empty() => T;` in a trait is a C# static abstract member, and one with a body a default (`static virtual`). Implementers write `static fn Empty() => Total = …`. They are called through a type parameter bounded by the trait, as in C#: `fn Concat<T: Monoid<T>>(items: List<T>) => T { mut result = T.Empty(); … }`. They can also be called on a type that implements the trait, including a default it doesn't override (`Total.Describe()`), but not on the trait itself. A trait with static methods can only be a bound, not the type of a value.
- **Builtin, .NET and runtime types implement traits too:** `extend int: Shape { fn Area() => double = … }`, and likewise `string`, `StringBuilder` or `Option` (`extend Option: Convert<string> { … }`). They get the trait's defaults (`3.Describe()`), convert to the trait (`let shapes: List<Shape> = [Circle(1.0), 2, 3];`), cast back from it (`(int)s`), and satisfy bounds (`Total([1, 2, 3])` for `fn Total<T: Shape>(items: List<T>)`, `Concat(["a", "b"])` for a `Monoid<T>` with static methods). Only Piper traits, not .NET interfaces: C# has no way to make `int` implement an interface, so a value used as a trait is wrapped in a generated box (see [How bounds are compiled](#how-trait-bounds-are-compiled)).

### Methods and `extend`

`extend` adds methods to an existing type: a record, an enum, a trait (see [Traits](#traits)), or a builtin such as `int` or `string`.

```piper
extend int {
    fn Squared() => int = this * this;
    static fn Zero() => int = 0;          // a static method, called as int.Zero()
}

extend Option {                           // generic types: their type parameters are in scope
    fn GetOr(fallback: T) => T = match this { Some(v) => v, None => fallback };
}

let n = 7.Squared();
let s = Option.Some(5).GetOr(0);
```

- **Operators:** an extend block for a record or enum can declare `+`, `-`, `*`, `/`, `%` and unary `-`, which become C# operators. The usual form takes `this` as the left (or only) operand; a `static operator` takes every operand as a parameter, one of them of the type, for when the type is on the right:

  ```piper
  extend Vector {
      operator +(other: Vector) => Vector = Vector(this.x + other.x, this.y + other.y);
      operator -() => Vector = Vector(-this.x, -this.y);
      operator *(k: double) => Vector = Vector(this.x * k, this.y * k);
      static operator *(k: double, v: Vector) => Vector = v * k;    // 2.0 * v
  }
  ```

  Operands convert as for a call (`2 * v` widens the int), the overload whose operand types match exactly wins, and `+=` and the other compound assignments use the operator. Comparisons and `==` can't be declared (records already compare by value); nor can operators on traits, builtin or .NET types.
- **Instance and static methods:** as in C#, a method is an instance method by default, with `this` referring to the receiver. Mark it `static fn` to call it on the type instead, e.g. `int.Zero()`. There is no explicit receiver parameter.
- **`this` is immutable.** Using it in a static method or a top-level function is an error.
- **Generic types:** write `extend Option`, not `extend Option<int>`. Methods apply to every instantiation.
- **Name clashes are errors.** A method can't share a name with a field, a variant, a variant's field (variants inherit their enum's methods) or another method.
- **Builtin members:** on builtin types, a method can't reuse a name the .NET type already has (such as `string.Length`), because the .NET member would silently win.

### Classes

A class is a reference type with identity and state that can change, for the objects records don't suit: a bank
account, a cache, a connection. It follows C#: a primary constructor, then fields and methods in the body.

```piper
class Account(owner: string, opening: decimal) {
    mut balance = opening;                 // private
    public let name = owner;               // readable from outside
    public mut deposits = 0;               // readable and settable from outside

    fn Deposit(amount: decimal) {          // public
        balance += amount;
        deposits += 1;
        Log("deposit {amount}");
    }

    fn Transfer(to: Account, amount: decimal) {
        balance -= amount;
        to.balance += amount;              // another Account's private field: visible inside Account
    }

    private fn Log(message: string) { Print("{owner}: {message}"); }

    static fn Empty(owner: string) => Account = Account(owner, 0m);
}

let ada = Account("Ada", 100m);
ada.Deposit(25m);
```

- **Making one:** call the class like a function, `Account("Ada", 100m)`, with named and default arguments as for any
  call. A class without parameters is made with `Counter()`. Generic classes take their type arguments explicitly or
  from the expected type: `Stack<int>()`, `let t: Stack<string> = Stack();`.
- **Constructor parameters** are in scope in every field initializer and instance method, as in a C# primary
  constructor. A field initializer can use the parameters but not other fields.
- **Fields:** `let` fields are read-only, `mut` fields can be assigned in methods (`balance += amount`). Fields are
  private unless marked `public`; a public field is a C# property, settable from outside when it's `mut`. The type
  may be left to the initializer.
- **Methods** are public unless marked `private`. Inside an instance method, fields, parameters and the other methods
  are named bare (`balance`, `Log(…)`), or through `this` (`this.name`), which is needed only when a parameter of the
  method hides a field. Static methods have no instance, so using a field or an instance method in one is an error.
- **Privacy is per class,** as in C#: methods of `Account` can use the private members of any `Account`.
- **Identity:** classes compare by reference. Two accounts with the same balance aren't equal, and passing one around
  passes the same object.
- **Inheritance:** a class can derive from one class, a Piper class or a .NET one, passing its constructor arguments:

  ```piper
  abstract class Shape(name: string) {
      abstract fn Area() => double;                                   // no body: derived classes supply it
      virtual fn Describe() => string = "{name} with area {Area()}";  // a default they may replace
  }

  class Circle(radius: double): Shape("circle") {
      override fn Area() => double = 3.0 * radius * radius;
  }

  class NotFound(id: int): System.Exception("No item {id}") { public let item = id; }
  ```

  - A class converts implicitly to its base classes (`let shapes: List<Shape> = [Circle(1.0)];`), and calls through
    the base dispatch to the override.
  - Only `virtual` and `abstract` methods can be overridden, by a method marked `override` with the same signature.
    Redeclaring an inherited method without `override` is an error, rather than C#'s warning and hiding.
  - `abstract fn` has no body, and only an `abstract class` can declare one. An abstract class can't be made itself,
    and a class deriving from it must override every abstract method or be abstract too.
  - A `sealed class` can't be derived from.
  - Overriding a .NET method works for public and protected virtual ones, such as `ToString`, `Equals`, or
    `Collection<T>.InsertItem`. The signature is the .NET method's, with the base class's type arguments substituted
    (`InsertItem(index: int, item: string)` for a `Collection<string>`).
  - Inside its methods, a class names the .NET members it inherits bare, as C# does (`Message`, `Items.Add(x)`), and
    can use protected ones (on itself, or another instance of its class). Outside, protected members stay hidden.
  - A class deriving from a .NET collection can be indexed, iterated and queried with LINQ like the collection.
  - A class deriving from `Exception` can be thrown and caught by its type.
  - A .NET base whose abstract members are properties (`Stream`) can't be completed yet, since classes can't declare
    properties.
- **Traits and interfaces** follow the base class in the list, and are implemented by the class's methods:
  `class Dog(name: string): Named { fn Name() => string = name; }`, or
  `class Version(major: int): IComparable<Version> { … }`. A class gets the traits' defaults like any implementer.
- **`extend`** can add methods to a class from elsewhere, as for records.

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
| `user?.Greet();` | as a statement, the call only happens when there is a value |
| `name ?? "anonymous"` | the value, or the fallback (evaluated only if needed); right-associative, so `a ?? b ?? c` works |
| `let u = Find(id)?;` | the value, or **return `None` from the enclosing function** (which must return an optional) |
| `x == null`, `x != null`, `x == 5` | comparisons; `x == 5` compares with `Some(5)` |
| `if x is Some(n) && n > 3 { … n … }` | C#-style `is` patterns. Variables bound in an `if`/`while` condition (or a match guard) are in scope in the body |
| `match x { null => …, Some(n) => … }` | `null` is also a pattern for `None`; exhaustiveness checks apply |

- **Inference through `T?`:** `fn F<T>(v: T?)` can be called as `F(5)`, which infers `T = int` and wraps the argument in `Some`.
- **`is` patterns elsewhere:** outside a condition, an `is` pattern can test but not bind, e.g. `let ok = x is Some(_);`.

### Errors as values: `Result`

```piper
enum Result<T, E> { Ok(value: T), Err(error: E) }    // built in; Ok(…) and Err(…) can be used unqualified

fn Compute(a: int, b: int) => Result<int, MathError> {
    let q = Divide(a, b)?;      // an Err returns from Compute immediately
    Ok(q * 10)
}
```

- **`?` propagates errors.** On a `Result`, `?` unwraps `Ok`, or returns the `Err` from a function that itself returns a `Result` with a compatible error type. A `Result` has no conditional access, so `Divide(a, b)?.ToString()` is `?` then `.ToString()`. On an `Option`, `?.` is always conditional access; write `(F()?).name` to unwrap first.
- **Pattern matching:** `Result` values match like any enum, e.g. `Err(MathError.DivideByZero)`.
- **No redeclaring:** `Option` and `Result` are built in, so they can't be redeclared. You can extend them with methods, e.g. `extend Option { fn IsNone() => bool = this is None; }`, and make them implement traits.
- **One runtime:** they're defined once, in the `Piper.Runtime` assembly (as `Piper.Runtime.Option<T>` and `Piper.Runtime.Result<T, E>`), which every compiled Piper program and library references. So Piper libraries can pass them to each other, and C# code can use them too.

### Programs, namespaces and visibility

A program is any number of `.piper` files compiled together, e.g. `piper run src/`. As in C#, each file
may start with a file-scoped namespace and `using` directives:

```piper
namespace Geometry;          // everything in this file is in Geometry

using System.Text;           // .NET namespaces...
using Geometry.Shapes;       // ...and Piper ones alike

record Point(x: double, y: double);
fn Area(s: Shape) => double = ...;
private fn Square(x: double) => double = x * x;   // visible only in this file
```

- **Namespaces span files.** Several files can declare the same namespace and see each other's declarations without a `using`. A file without a `namespace` contributes to the global namespace.
- **Qualified names always work**, without a `using`, in both expressions and types: `Geometry.Area(s)`, `Geometry.Point(1.0, 2.0)`, `System.Text.StringBuilder`.
- **`using N;` imports** N's types and functions, but not its sub-namespaces, as in C#. A name imported from two namespaces is an error where it's used; qualify it.
- **Lookup order:** this file's private declarations, then the file's namespace and the namespaces enclosing it, then Piper's builtins, then imports.
- **Visibility:**
  - Declarations are public by default.
  - `private` restricts a function, record, enum, trait or extend-block method to its own file.
  - A public declaration can't expose a private type in its signature or fields. Make it private too, or make the type public.
- **`extend` works across files.** An `extend` block can extend a type declared in another file (the emitted C# uses `partial` types).
- **One entry point:** exactly one `Main`, in any namespace.

### .NET interop

Any public type in the .NET base libraries can be used. So can the types of the NuGet packages, shared frameworks
(such as ASP.NET Core) and assemblies a program folder's `piper.json` lists in `"packages"`, `"frameworks"` and
`"references"`. Builtin Piper types are the corresponding .NET types, so their members are available too.

A lambda whose parameters all have types can be passed where .NET takes any `Delegate`, as in C#. ASP.NET Core's
minimal APIs rely on this:

```piper
app.MapGet("/hello/\{name}", (name: string) => "Hello {name}");   // \{ is a literal brace, as {…} interpolates
```

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
- **`extend` on .NET types:** a non-generic .NET type can be extended like a builtin, e.g. `extend StringBuilder { fn Line(s: string) { this.AppendLine(s); } }`, but not with a member name it already has.
- **Generic and extension methods:** type arguments are inferred from the arguments, including lambdas, and extension methods from imported namespaces apply, so LINQ works: `numbers.Where(n => n > 2).Select(n => n * 10).ToList()`.
- **Indexers and setters:** `list[0]`, `ages["Ada"] = 36`, `ages["Ada"] += 1` and `builder.Length = 0` use .NET indexers, arrays and property setters.
- **Constructors of builtin types** work like any other: `string('#', 3)` is C#'s `new string('#', 3)`.
- **Explicit type arguments** work for .NET too: `Dictionary<string, int>()`, `Enumerable.Empty<int>()`, `xs.Select<int, string>(…)`.
- **`out` parameters,** as in C#: `out let n` declares `n` (`out mut n` declares it mutable), `out n` assigns an existing `mut` variable of exactly the parameter's type, and `out _` discards. A variable declared in an `if` condition stays in scope after the `if`, as in C#, so guards work:
  ```piper
  if !int.TryParse(text, out let n) { return; }
  Print(n * 2);
  ```
  Where .NET may write null (`out string?`), the variable is optional. `[MaybeNullWhen(false)]`, as on `Dictionary.TryGetValue`, means null only when the call returns false, so where it returned true the variable is a plain value: in the body of `if names.TryGetValue(key, out let name) { Print(name.Length); }` (or a `while`, or later in an `&&` chain), and after a guard `if !names.TryGetValue(key, out let name) { return; }`. Anywhere else it is optional. Piper functions have no `out` parameters; they return several values as a [tuple](#tuples-and-deconstruction).
- **`in` parameters** take ordinary arguments.
- **`ref` parameters** take `ref x`, where x is a `mut` variable of exactly the parameter's type, or an array element: `Interlocked.Increment(ref counter)`, `Array.Resize(ref items, 10)`. .NET's nullability on a ref parameter is ignored, since the variable already has a type.
- **Operators** a .NET type declares work as in C#: `date > other` on DateOnly, `end - start` on DateTime (a TimeSpan), `span += TimeSpan.FromHours(1)`, BigInteger arithmetic. Its implicit conversions don't apply yet, so write `BigInteger(2)` rather than passing `2` where a BigInteger is expected.
- **Events:** `list.CollectionChanged += handler` adds a handler and `-=` removes it, as in C#. The handler is a lambda or a function, converted to the event's delegate type. An event can't be read or assigned with `=`.

### Attributes

.NET attributes go before a declaration, field or parameter, as in C#:

```piper
using System;
using System.Text.Json.Serialization;

record Person([JsonPropertyName("first_name")] first: string, age: int);

[Obsolete("Use parse.")]
fn LegacyParse(text: string) => int = int.Parse(text);
```

- **Names:** `Obsolete` means `ObsoleteAttribute` (tried first) or `Obsolete`, found like any type name, so it needs a `using` or a qualified name (`System.Obsolete`). It must be a .NET attribute class; Piper can't declare attributes.
- **Arguments** are the constructor's, which must be constants (literals, .NET enum members such as `AttributeTargets.Method`, constant fields, and operators on those), followed by `Name = value` for the attribute's settable properties and fields: `[Obsolete("Old", DiagnosticId = "APP001")]`. Several attributes can share brackets: `[Serializable, Obsolete]`.
- **Where they go:** on records, enums and traits (C# classes and interfaces), functions and methods, parameters, and record and variant fields. A field's attribute goes on the C# property when it applies to properties (`[property: …]`, so System.Text.Json and similar see it), otherwise on the backing field, otherwise the constructor parameter. An attribute that can't go where it's written (`[Serializable]` on a function) is an error, as is repeating one that doesn't allow it.
- **`[Obsolete]`** on a Piper declaration makes each use of it a warning, `'F' is obsolete: message` (an error with `[Obsolete("…", true)]`), as in C#. Uses of obsolete .NET types, methods, constructors and properties are reported the same way (`'WebClient' is obsolete: … Use HttpClient instead.`).

### Named arguments and default values

As in C#, a parameter (or record field) can have a default value, and a call can name its arguments:

```piper
fn Connect(host: string, port: int = 443, retries: int = 3) => Connection = …;

Connect("example.com");
Connect("example.com", retries: 5);
Connect(retries: 1, host: "localhost");

record Options(verbose: bool = false, label: string? = null);
let quiet = Options();
```

- **Defaults** must be constants (literals, .NET constants and enum members, operators on those) or `null` for an optional, and come after the parameters without them. Operators can't have them.
- **Named arguments** come after the positional ones, each naming a parameter once; parameters with defaults can be left out. Arguments are evaluated in the order they're written, whatever order the parameters are in.
- **They work for** Piper functions, methods and records, and .NET methods and constructors (`Math.Round(x, digits: 2)`, `DateTime(year: 2026, month: 3, day: 14)`). Not for function values (lambdas and delegates) or attributes, whose properties are set with `Name = value`.
- **Through traits:** a trait method's defaults apply when it's called through a generic bound too.

### Lambdas and function values

A function type `(int, int) => bool` is the .NET delegate `Func<int, int, bool>`, and one returning unit is an
`Action`. They pass straight to .NET APIs, and other delegate types (`Comparison<T>`, `Predicate<T>`) are accepted where expected.

```piper
let increment: (int) => int = x => x + 1;     // parameter types come from the expected type
let add = (a: int, b: int) => a + b;          // or from annotations: add is (int, int) => int
let total = Apply(Doubled, 21);              // a top-level function is a value too
let twice: (int) => int = identity;           // a generic one takes its type arguments from the expected type
let echo = identity<string>;                  // or from explicit ones
names.Sort((a, b) => a.Length - b.Length);    // a .NET delegate type
```

- A lambda's return type is the expected one, or else the common type of its body's value and its `return`s.
- Lambdas capture variables by reference, as in C#, so `n => { total += n; }` updates `total`.
- Each iteration of a `for` loop has its own loop variable, over a range as over a collection, so
  `for i in 0..<3 { actions.Add(() => i); }` captures 0, 1 and 2.
- An error in a lambda passed to an overloaded .NET method is reported in the lambda, not as "no overload accepts
  these arguments", when its parameters say which overload is meant.
- A lambda without annotations needs an expected function type; a lambda with too few or many parameters for it is an error.

### Local functions

A function can be declared inside a block, as in C#:

```piper
fn Report(items: List<int>) {
    let limit = 10;
    fn Fib(n: int) => int = if n < 2 { n } else { Fib(n - 1) + Fib(n - 2) };   // can call itself
    fn Over(n: int) => bool { n > limit }                                       // captures `limit`
    for item in items {
        if Over(item) { Print("{item}: too big"); } else { Print(Fib(item)); }
    }
}
```

- **Scope:** usable anywhere in its block, even before the declaration, so local functions can call each other.
- **Captures:** the variables around it, by reference, as a lambda does. In a loop, each iteration's variable.
- **Signatures:** can be generic, `async` or iterators, take defaults and `requires` clauses, and be passed as values,
  like any function. `return` leaves the local function.
- **Layout:** write it indented. An `fn` at the very start of a line inside a block is taken to be the next top-level
  function, so a forgotten `}` gives one clear error rather than swallowing the rest of the file.
- **In C#:** each is a C# local function.

### Trailing closures

A block after a call is its last argument, as a lambda, as in Kotlin and Swift. This lets a library function read like
a statement:

```piper
Repeat(3) { i => Print(i); }                  // Repeat(3, i => { Print(i); })
Guarded { RiskyWork(); }                      // Guarded(() => { RiskyWork(); }): the closure is the only argument
let total = Measure("sum") { numbers.Sum() }; // the block's value is the closure's
names.Sort() { (a, b) => a.Length - b.Length }
```

- The block may start with lambda parameters and `=>`. Without them, the closure has none.
- A statement ending with a trailing closure needs no `;`.
- In an `if`, `while`, `for`, `match` or `requires` head, a `{` is the body (`if ready { … }`). A closure there must be
  inside brackets.
- After the closure, `.`, `?.` and `?` continue the expression. A `(`, `[` or `{` starts the next statement.

- A closure that awaits (outside a lambda of its own) is async, as if written `async () => { … }`. One that doesn't is
  synchronous, even in an `async fn`.

It's only syntax: the call is bound exactly as if the lambda were written in the parentheses.

### Plugins

A compiler plugin, listed in `piper.json`'s `"plugins"`, adds keywords that start a statement or a top-level
declaration, such as `parallel for x in items { … }`. The plugin rewrites its construct into ordinary Piper as it's
parsed, so everything after parsing, including type checking and errors, works as for code the user wrote. A plugin
keyword is still an ordinary name wherever its construct can't start. See [plugins.md](plugins.md).

### Collections

- **Literals:** `[1, 2, 3]` is a `List<int>`. The element type comes from the expected type (`List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, … or a .NET array) or else the elements' common type. `[]` needs an expected type.
- **Spread:** `[0, ...xs, 4]` puts every item of a sequence (a list, array, string, LINQ result or iterator: anything that is an `IEnumerable<T>`) into the literal, in order, as C#'s `[0, ..xs, 4]`. Its items count towards the common element type, and widen as C# allows (`let ys: long[] = [...ints];`), but not to an optional or a tuple of other element types.
- **Ranges:** `for i in 1..10` counts from 1 to 10, including 10, as in Swift; `for i in 0..<items.Count` stops before the end, for indexes. Ranges only appear in `for` loops and patterns.
- **`for x in collection`** works on anything implementing `IEnumerable<T>`: lists, arrays, strings (as `char`s), dictionaries and LINQ results.
- **Indexing:** `xs[i]` on arrays and on any .NET type with an indexer.
- **Arrays:** `int[]` is an array of int and `int[,]` a two-dimensional one, as in C#. `let xs: int[] = [1, 2, 3];` makes one from a literal, and `int[5]` or `double[2, 3]` creates one of default values, like C#'s `new int[5]`. That needs an element type whose default isn't null (numbers, bool, char, optionals, .NET structs and tuples of those): `string[3]` is an error, as its elements would be null, but `string?[3]` (three `None`s) is fine.

### Tuples and deconstruction

Tuples are C#'s value tuples. Elements can be named, and are read by name or as `Item1`, `Item2`, …. Tuples from .NET keep their element names, so `Math.DivRem(20, 6).Quotient` works.

```piper
fn Divide(a: int, b: int) => (quotient: int, remainder: int) = (a / b, a % b);

let result = Divide(17, 5);
Print("{result.quotient} r {result.remainder}");
let (q, r) = Divide(7, 2);                    // deconstruction: tuples, records, .NET types with Deconstruct
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

async fn PageLength(client: HttpClient, url: string) => Task<int> {
    let body = await client.GetStringAsync(url);
    body.Length
}

async fn Main() {                              // no return type: a plain Task
    using let client = HttpClient();
    let lengths = await Task.WhenAll(["https://a.example", "https://b.example"].Select(async url => await PageLength(client, url)));
    Print(lengths.Sum());
}
```

- **`await x`** works on anything awaitable by C#'s rules: a Task, a ValueTask, `ConfigureAwait(false)`, or any type with a `GetAwaiter()`. Its value is the result (`int` for a `Task<int>`; nothing for a plain `Task`). It can appear anywhere in an expression: `if await Ready() { … }`, `"{await Name()}"`.
- **Async lambdas:** `async x => await F(x)` and `async () => { … }`, as values of `(int) => Task<int>` and the like, or for .NET APIs such as `Task.Run` and LINQ.
- **`async fn Main()`** may be the entry point.
- **`await for x in stream { … }`** iterates an `IAsyncEnumerable<T>` (C#'s `await foreach`), and **`await using`** disposes an `IAsyncDisposable` with `DisposeAsync`.
- `await` only works in an async function or lambda. An async function with no `await` gets a warning, as in C#, since it runs synchronously. `async` and `await` are contextual keywords, as in C#, so they can still be names.
- A trait's required methods can't be `async` (it's part of a body, not a signature), but an implementation can be.

### Iterators: `yield`

As in C#, a function whose body contains `yield` is an iterator: it returns `IEnumerable<T>` (or `IEnumerator<T>`, or with
`async` `IAsyncEnumerable<T>`), produces values with `yield return` and stops with `yield break` or at its end. It runs
lazily, a step at a time as the caller iterates.

```piper
fn Fibonacci() => IEnumerable<long> {
    mut (a, b) = (0L, 1L);
    while true {                              // endless: callers take what they need
        yield return a;
        (a, b) = (b, a + b);
    }
}

Print(string.Join(", ", Fibonacci().Take(10)));

async fn Ticks(count: int) => IAsyncEnumerable<int> {
    for i in 1..count {
        await Task.Delay(100);
        yield return i;
    }
}

await for tick in Ticks(3) { Print(tick); }
```

- An iterator can't `return`; `yield break` ends it early.
- As in C#, `yield return` can't be inside a `try` that has a `catch`, and `yield` can't be in a `catch` or `finally`; a `try`/`finally` around `yield return` is fine (the `finally` runs when the caller stops iterating).
- Lambdas can't be iterators.

### Exceptions

Exceptions are .NET exceptions, for code that crosses into .NET; within Piper, prefer `Result`.

```piper
try {
    Print(Divide(1, 0));
} catch (e: ArgumentException) {
    Print("caught: {e.Message}");
} catch (_) {                                 // no type: any exception; `_` binds nothing
    Print("something else");
} finally {
    Print("always");
}
```

- `throw e` throws any value deriving from `System.Exception`. It is an expression of type Never, so it fits anywhere a value is expected: `if ok { v } else { throw InvalidOperationException("…") }`.
- `catch (e)` without a type catches `System.Exception`. A clause made unreachable by an earlier one is an error, as in C#.
- `break`, `continue`, `return` and `?` can't leave a `finally` block.

### Disposing resources: `using`

As in C#, `using` disposes a value that implements `System.IDisposable` when it goes out of scope.

```piper
fn CountLines(path: string) => int {
    using let reader = File.OpenText(path);   // disposed when the function returns, by any path
    mut count = 0;
    while reader.ReadLine() is Some(_) {
        count += 1;
    }
    count
}

using (let writer = StringWriter()) {         // disposed after the block
    writer.Write("hello");
}
```

- A `using let` (or `using mut`) variable is disposed at the end of the block that declares it, even when that block is an expression, and can't be reassigned.
- `using (expression) { … }` disposes the value of any expression after the block.

### Cleaning up: `defer`

`defer` runs code when the enclosing block ends, however it ends: by reaching its end, `return`, `break`, `continue`, `?`
or an exception. It is Swift's `defer`, and C#'s `try`/`finally` without the nesting.

```piper
fn Copy(from: string, to: string) {
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

- **`let … else`** unwraps a value or leaves: `let Some(user) = Find(id) else { return Err("no user"); };` binds `user` for the rest of the block when the value matches the pattern, and otherwise runs the `else` block, which must leave (`return`, `break`, `continue` or `throw`). Any pattern that starts with `[` or a name followed by `(`, `.` or `{` works: `let [first, ...rest] = items else { return 0; };`, `let Circle(r) | Ring(r, _) = shape else { … };`. The else block can't see the pattern's variables, and a pattern that always matches is a warning.
- **Destructuring with `let`:** a pattern that always matches needs no else: `let Point(x, y) = p;`, `let Line(Point(x1, _), _) = line;`, `let Point { y: height } = p;`. One that can fail, such as `let Some(v) = i;`, is an error that suggests adding an else (there's no unchecked unwrap).

- `let` declares an immutable binding and `mut` a mutable one. Assigning to a `let` binding, a parameter or a `for` loop variable is a compile error.
- Shadowing is allowed in nested scopes (`let x = x + 1;` inside a block), but redeclaring a name in the same scope is not.
- Declarations at the top level can appear in any order.

### Expressions everywhere

- **Blocks are expressions.** A block's value is its final expression, written without a `;`: `let y = { let t = 2; t * 3 };`.
- **`if` is an expression.** `let s = if c { "a" } else { "b" };` gives both branches' common type. An `if` without `else` has type unit.
- **Function bodies follow the same rule.** A function body's final expression is its return value. A function with a return type must end in a value or return on every path; as in C#, a `while true` loop with no `break` never ends, so a function may end with one.
- **Assignment is a statement-like expression of type unit,** so `a = b = c` is rejected.
- **Conditions must be `bool`.** There is no truthiness.

### The pipe operator: `|>`

`x |> F(a, b)` is `F(x, a, b)`: the value on the left becomes the first argument of the call on the right, and
`x |> F` is `F(x)`. A chain of calls then reads in the order it runs.

```piper
let total = orders
    |> Enumerable.Where(o => o.paid)
    |> Enumerable.Sum(o => o.amount)
    |> Round(2);
```

- The right side is anything that can be called: a function, a function value, a record, or a .NET method (`|> Math.Abs`, `|> Enumerable.ToList()`).
- It binds looser than arithmetic and tighter than comparison, so `a + b |> F == c` is `F(a + b) == c`. Chains go left to right.
- It is plain call syntax, so it emits an ordinary C# call.

### Evaluation order

Evaluation is strictly left to right, including operands that contain blocks.
- `x += e` reads `x` before evaluating `e`.
- `x |> F(a)` evaluates `F`, then `x`, then `a`, as the call `F(x, a)` would.
- The right-hand side of `&&` or `||` is evaluated only when needed.
- The upper bound of `for i in a..<b` is evaluated once.

### Preconditions and invariants

A function, method or operator can state what its arguments must satisfy, and a record what its fields must, with
`requires` clauses:

```piper
fn Sqrt(x: double) => double
    requires x >= 0.0
= Math.Sqrt(x);

fn Withdraw(amount: decimal, balance: decimal) => decimal
    requires amount > 0
    requires amount <= balance
= balance - amount;

record Percentage(value: int) requires value >= 0 && value <= 100;
```

- **Conditions** are `bool` expressions over the parameters (and `this` in an instance method), or a record's fields. As in an `if`, they can bind with `is`: `requires count is Some(n) && n > 0`.
  Separate several with commas, or write several `requires` clauses; each is checked on its own.
- **At run time** every call checks its function's preconditions before the body runs. A broken one throws
  `ArgumentException`, saying which condition failed and the values it used, and naming the parameter when there's
  only one: `Precondition failed: x >= 0.0 (x = -1) (Parameter 'x')`. The stack trace points at the `requires` line. In an
  async function or iterator the check runs where C# runs a body's first statement: in the Task, or on the first item.
- **Records** are checked whenever one is made, including by a copy: `{ ...p, value: 200 }` is checked like
  `Percentage(200)`, and fails with `Invariant failed: value >= 0 && value <= 100 (value = 200)`. So a Percentage outside
  0 to 100 can't exist in Piper code. (C# code using `with` on the record isn't checked.)
- **At compile time** a call whose arguments are constants that break a condition is an error (PIP0340), such as
  `Sqrt(-1.0)` or `Percentage(150)`. Calls that can't be decided this way are left to run time.
- **Not yet:** trait methods (PIP0341), enum variants and lambdas can't have `requires` clauses.

### Tests

A `test` block checks something, with `Assert` and `AssertEqual`. `piper test` runs them; other builds type-check them
but leave them out, so they cost nothing in the program.

```piper
using System.Threading.Tasks;

fn Add(a: int, b: int) => int = a + b;

test "adds two numbers" {
    AssertEqual(5, Add(2, 3));
    Assert(Add(-1, 1) == 0);
}

async test "waits" {
    await Task.Delay(10);
    Assert(true, "still here");
}
```

- **`Assert(condition)`** fails the test when the condition is false, reporting its source text: `Assertion failed: Add(-1, 1) == 0`. `Assert(condition, message)` reports the message instead.
- **`AssertEqual(expected, actual)`** fails unless the values are equal (by `Equals`, as `==` compares). Sequences other than strings are compared element by element, so `AssertEqual([1, 2], xs)` works on a List. They must have a common type. A failure shows both: `Expected [1, 2] but got [1, 2, 3]`.
- **Running:** `piper test src/` runs every test in order and prints `PASS` or `FAIL` for each, with the reason and the Piper line for a failure, then a count. It exits with 1 if any failed. `--filter text` runs only the tests whose name contains `text`. An exception fails a test too.
- **Names** are plain strings (no interpolation), unique in the program. A test can't be called, takes no parameters and returns nothing; `async test` can `await`.

### Builtins

`Print(value)` writes any value followed by a newline, using its C# `ToString`: Bools print as `True`/`False`,
records as `Point { x = 1, y = 2 }`, fieldless enum variants by name (`Food`, as a C# enum member) and tuples as `(1, a)`. Output
is locale-independent. `Assert` and `AssertEqual` are for tests (see [Tests](#tests)), though they work anywhere: a failure throws.
`Main()` returns unit and is the program's entry point. It takes no parameters, or the command-line arguments as `Main(args: string[])`, as in C#; `piper run app -- one two` passes `one` and `two`.

As in C#, functions and methods are PascalCase (`Print`, `Main`, `Area()`), and variables, parameters and fields camelCase. That's a convention, not a rule: the compiler accepts any casing. A method added to a .NET or builtin type mustn't have the name of one of its members (`extend int { fn Clamp(…) }` clashes with `int.Clamp`).

## How Piper maps to C#

| Piper | C# |
|---|---|
| `record P(x: int)` | `public sealed record P(int x);` |
| `class A(x: int): B(x), Named { mut n = x; public let m = 1; … }` | `public partial class A(int x) : B(x), Named { private int n = x; public int m { get; } = 1; … }` |
| `virtual fn F()`, `override fn F()`, `abstract fn F();` | `public virtual void F()`, `public override void F()` (`protected` for a protected .NET method), `public abstract void F();` |
| `{ ...p, y: 5 }` | `p with { y = 5 }` |
| `[0, ...xs]` | `(List<int>)[0, .. xs]` |
| `enum Shape { Circle(r: double), Empty }` | `public abstract record Shape` with a private constructor and nested `public sealed record Circle(double r) : Shape;`, `public sealed record Empty() : Shape;` |
| `Shape.Circle(1.0)` | `new Shape.Circle(1.0)` |
| `match s { Circle(r) if r > 1.0 => a, Empty => b, … }` | `if (s is Shape.Circle(var r) && r > 1.0) { … } else if … else { … }` |
| patterns `1 \| 2`, `1..9`, `_`, `< 0` | C# patterns `1 or 2`, `>= 1 and <= 9`, `_`, `< 0` |
| `[Obsolete("x")] fn F()`, `record P([JsonPropertyName("id")] x: int)` | `[global::System.ObsoleteAttribute("x")] public static void F()`, `record P([property: global::…JsonPropertyNameAttribute("id")] int x)` |
| `[first, ...rest]`, `[_, ..., last]` | list patterns `[var first, .. var rest]`, `[_, .., var last]` |
| `Point { x: 0 }`, `{ Length: > 5 }` | `Point(0, _)` (positional, for a Piper record or variant), `{ Length: > 5 }` |
| `record Pair<A, B: Show>(…)`, `fn F<T: IComparable<T>>` | `public sealed record Pair<A, B>(…)`: trait bounds aren't C# constraints (see below); .NET interface bounds are, `where T : IComparable<T>` |
| `Swap(p)` (generic) | `Swap<int, string>(p)`: type arguments are always explicit |
| `(int, string)`, `(x: int, y: int)`, `(1, "a")`, `t.x` | `(int, string)`, `(int x, int y)`, `(1, "a")`, `t.Item1` (always by position) |
| `let (a, b) = t;`, `(a, b) = (b, a);`, `for (k, v) in d` | `var (a, b) = t;`, `(a, b) = (b, a);`, `foreach (var (k, v) in d)` |
| `int[]`, `int[5]`, `double[2, 3]` | `int[]`, `new int[5]`, `new double[2, 3]` |
| `int.TryParse(s, out let n)` | `int n;` before the statement, then `int.TryParse(s, out n)`; an optional `out` goes through `PiperInterop.AfterOutReference(…)` |
| `Interlocked.Increment(ref n)`, `list.Changed += h` | the same: `ref n`, `list.Changed += h` |
| `trait Shape { fn Area() => double; fn D() => string = …; }` | `public interface Shape { double Area(); string D() => …; }` |
| `trait Ordered<T: Ordered<T>>: Equatable<T> { … }` | `public interface Ordered<T> : Equatable<T> { … }`, with nested witness types `PiperWitness<TPiperSelf>`, `PiperVia<TPiperSelf>` and `PiperMapped<TPiperSelf, TPiperBox>` |
| `trait Formal: Named { fn Greet() => string = …; }` (an override) | `public interface Formal : Named { string Named.Greet() => …; }` (an explicit implementation) |
| `trait Monoid<T: Monoid<T>> { static fn Empty() => T; static fn D() => string = …; }` | `interface Monoid<T> { static abstract T Empty(); static virtual string D() => …; }` |
| `x.Area()`, `T.Empty()` and `let s: Shape = x;` for `x: T`, `T: Shape` | `PiperTraits.Witness<Shape.PiperWitness<T>>().Area(x)`, `….Empty()` and `….PiperBox(x)` |
| `extend int: Shape { … }` | the methods as extension members, plus `file readonly record struct PiperBox_int(int Value) : Shape, IPiperBox<int, PiperBox_int>` forwarding to them, registered by a `[ModuleInitializer]` |
| `(int)s` for `s: Shape` | `PiperTraits.Unbox<int>(s)` |
| `extend Money: IComparable<Money> { fn CompareTo(other: Money?) => int … }` | `record Money : IComparable<Money>` with `public int CompareTo(Option<Money> other)`, plus `int IComparable<Money>.CompareTo(Money? other) => CompareTo(PiperInterop.FromNullableReference(other));` where nullability differs |
| `extend Circle: Shape { fn Area() … }` | members inside `public sealed record Circle(…) : Shape { public double Area() … }` |
| `c.D()` where `D` is an un-overridden default | `((Shape)c).D()` (C# only exposes default interface methods through the interface) |
| `extend int { fn Squared() … static fn Zero() … }` | C# 14 `extension(int self) { public int Squared() … }` and `extension(int) { public static int Zero() … }` |
| `this` | `this` in members; in C# extension blocks the receiver parameter is named `self`, since C# reserves `this` |
| `int?`, `Result<int, E>` | `Option<int>`, `Result<int, E>`: small `readonly record struct`s added to the output when used (not C#'s `int?`, which can't represent `None` for an unconstrained generic `T` or nest) |
| `Some(x)`, `None`, `null` | `Option<int>.Some(x)`, `Option<int>.None` |
| `match o { Some(v) => …, None => … }`, `o is Some(v)` | property patterns `o is { IsSome: true, Value: var v }`, `{ IsSome: false }` |
| `a ?? b`, `a?.b` | `a.IsSome ? a.Value : b`, `a.IsSome ? Option<T>.Some(a.Value.b) : Option<T>.None` (non-variable operands are first stored in a temporary) |
| `let x = F()?;` | `var t = F(); if (!t.IsSome) { return Option<R>.None; } x = t.Value;` |
| a `.piper` file | a `.cs` file of the same name, starting `using Piper.Runtime;` (for Option, Result and the `PiperInterop` helpers); the file with `Main` also gets the entry point |
| `async fn F() => Task<int>`, `await t` | `async Task<int> F()`, `await t` |
| `async fn Main()` | an `async Task Main()`, run by `Main().GetAwaiter().GetResult()` |
| `await for x in s`, `await using let r = …;` | `await foreach (T x in s)`, `await using T r = …;` |
| `yield return x;`, `yield break;` | the same, in an iterator method returning `IEnumerable<T>` (or `IAsyncEnumerable<T>`) |
| `extend Option { … }` | C# 14 `extension<T>(Option<T> self) { … }`, since Option lives in Piper.Runtime |
| `namespace Geometry;` | `namespace Geometry { … }`; names from other namespaces are written `global::Geometry.Point` |
| `private record R(…)` / `private trait T` | `file sealed record R(…)` / `file interface T` (C# 11 file-local types) |
| `private fn F()` / private method | `internal static` in the file's module class / `internal` member |
| `extend` of a type declared in another file | that type becomes `partial`; the other file holds a `partial record` (or, for a trait, `partial interface`) part with the added members |
| `extend Named { fn Shout() … }` | default interface methods, in `Named` itself or a `partial interface Named` part |
| `Math.Sqrt(2.0)`, `StringBuilder()`, `s.Length` | `global::System.Math.Sqrt(2.0)`, `new global::System.Text.StringBuilder()`, `s.Length` |
| a possibly-null .NET result / a `T?` passed to .NET | `PiperInterop.FromNullableReference(…)` / `PiperInterop.ToNullableReference(…)` (and `…Value` versions for `Nullable<T>`) |
| `fn F(a: int) => int { … }` | `public static int F(int a) { … }` in a static module class |
| `fn F(a: int) => int = e;` | `public static int F(int a) => e;` |
| `let x = e;` / `mut x = e;` | `int x = e;` (immutability is checked by Piper, not C#) |
| `if` expression, simple branches | `c ? a : b` |
| `if` expression, branches with statements | a variable assigned in each branch, or `return` in each branch |
| `for i in a..<b` / `a..b` | `for (int i = a; i < b; i++)` / `<=` (non-constant bounds hoisted) |
| `for x in xs` | `foreach (int x in xs)` |
| `[1, 2]` | `new List<int> { 1, 2 }` (or `new T[] { … }` for an expected array), which needs no target type, unlike C#'s `[1, 2]` |
| `(int) => bool`, `x => x > 0` | `Func<int, bool>`, `(int x) => x > 0`: parameter types are always written |
| `F` used as a value | the method group `F` |
| `xs.Where(n => n > 2)` (extension method) | `Enumerable.Where<int>(xs, (int n) => n > 2)`: called statically, type arguments explicit |
| `try`/`catch (e: E)`/`finally`, `throw e` | `try`/`catch (E e)`/`finally`, `throw e;` (or a C# throw expression in `?:`) |
| `x \|> F(a)` | `F(x, a)` |
| `defer d; rest` | `try { rest } finally { d }`, with the block's value stored in a temporary inside the `try` |
| `"…{e}…"` | `$"…{e}…"` |
| `test "adds" { … }` | with `piper test`, `[global::Piper.Runtime.PiperTestAttribute("adds")] public static void PiperTest_N() { … }` in the module class; otherwise nothing |
| `Assert(c)`, `AssertEqual(a, b)` | `global::Piper.Runtime.PiperAssert.True(c, "c")`, `global::Piper.Runtime.PiperAssert.Equal<T>(a, b)` |
| `fn F(x: int) => int requires x > 0 = …` | `public static int F(int x) { global::Piper.Runtime.PiperContract.Require(x > 0, "x > 0", $"x = {x}", "x"); return …; }` |
| `record P(v: int) requires v >= 0;` | `record P(int v) { public int v { get; init; } = global::Piper.Runtime.PiperContract.Invariant(v, v >= 0, "v >= 0", …); }`, and copies construct a new P instead of using `with` |
| `extend V { operator +(o: V) => V = … }` | `public static V operator +(V self, V o) => …` in the record, with `this` as `self` |
| `let Some(u) = e else { return; };` | `T u; if (e is { IsSome: true, Value: var u1 }) { u = u1; goto matched; } { return; } matched: ;` |
| `fn F(a: int, b: int = 2)`, `F(1, b: 5)` | `static int F(int a, int b = 2)`, `F(1, b: 5)` (C#'s named arguments, so the written order is kept) |
| `/// Adds.` above `fn Add` | `/// <summary>Adds.</summary>` above `Add` |
| `Print(x)` | `global::System.Console.WriteLine(x)` |
| `(byte)x`, `1.5f`, `19.99m`, `string('#', 3)` | `(byte)x`, `1.5f`, `19.99m`, `new string('#', 3)` |
| `using let r = Open();`, `using (let r = Open()) { … }` | `using T r = Open();`, `using (T r = Open()) { … }` |
| `a == b` on a type parameter or trait | `EqualityComparer<T>.Default.Equals(a, b)` |
| `A(x) \| B(x) => body` | one `if` branch per alternative, each with the body (C# can't declare variables in an `or` pattern) |
| with `#line` directives (`piper run`, `piper build`, Piper.Build) | `#line 12 "hello.piper"` before each statement, so errors, stack traces and debuggers show Piper lines |

- **Locals** get unique C# names (`x`, `x_1`, …), because C# forbids shadowing.
- **Identifiers that are C# keywords** are escaped (`@class`).
- **The module class** is named after the source file (`hello.piper` → `Hello`). A `PiperEntryPoint.Main` calls the program's `Main`.

- **No double-checking:** Piper checks exhaustiveness and reachability itself. C# treats a switch arm it considers subsumed as an error, and its analysis isn't identical to Piper's.
- **Jumps:** a `break` inside a C# `switch` statement would exit the switch rather than the enclosing loop.
- **Shared lowering:** the chain reuses the lowering already used for `if`, which handles arms with statements, returns and values.

The match is known to be exhaustive, so a final arm without a guard or bindings becomes a plain `else`. Otherwise the chain ends in `throw new UnreachableException()`.

A guard that needs statements (a block, `?`, or `?.`/`??` on a call) can't go in an `if` condition, so its arm becomes nested
ifs: `if (s is P(var x)) { …; if (guard) { body; goto end; } }`, followed by the later arms and an `end: ;` label. An `if` or
`while` whose condition binds variables with `is` and then needs statements is lowered the same way, so C# still knows the
variables are assigned.

### How trait bounds are compiled

A bound `T: Shape` can be met by `int`, which C# can't make implement the `Shape` interface, so trait bounds aren't C#
constraints. Generic code reaches a trait through a *witness* for its type parameter instead:

- **The witness interface:** each trait has a nested `Shape.PiperWitness<TPiperSelf>`, with the trait's methods taking the value as their first parameter (static methods take none), and `PiperBox`, which turns the value into a `Shape`.
- **Calls:** `x.Area()` on a `T` becomes `PiperTraits.Witness<Shape.PiperWitness<T>>().Area(x)`. `PiperTraits` (in Piper.Runtime) finds the witness once per type and caches it in a static field, so a call costs a field read and an interface call.
- **Piper types:** records, enums and traits implement the interface themselves, and use `Shape.PiperVia<TPiperSelf>`, which calls it. Overrides and defaults dispatch as usual.
- **Builtin, .NET and runtime types:** these use `Shape.PiperMapped<TPiperSelf, TPiperBox>`. It wraps the value in a generated `readonly record struct` box that implements `Shape` by calling the type's extend methods. Wrapping an `int` in the struct allocates nothing, but a default method or a conversion to `Shape` puts the box on the heap. Each file registers its boxes' witnesses in a `[ModuleInitializer]`.
- **.NET interface bounds:** `T: IComparable<T>` stays a C# constraint, since .NET types implement those themselves.
- **Calling from C#:** C# code can call a Piper generic function with any type argument. One that doesn't implement the trait fails at run time, when its witness is first looked up.

## Known limitations

None known.

## Roadmap

1. ~~**Sum types and `match`**~~, done in milestone 2.
2. ~~**Null safety and errors**~~, done in milestone 4: `Option`/`T?`, `Result`, `null`, `?.`, `??`, `?` and `is` patterns.
3. ~~**Traits, generics and extensions**~~, done in milestone 3.
4. ~~**Modules, imports and .NET interop**~~, done in milestone 5: `namespace`, `using`, `private`, multi-file programs, and calling .NET.
5. ~~**Collections and lambdas**~~, done in milestone 6: lambdas and function types, LINQ, collection literals, `for x in collection`, indexing, property setters, and `try`/`catch`/`throw`.
6. ~~**Language freeze**~~, done in milestone 7: C#-compatible printing and minimum-value literals, arrays (`int[]`, `int[5]`), explicit type arguments, generic functions as values, tuples and deconstruction, and `out` parameters.
7. ~~**Robustness and tooling**~~, done in milestone 8: `#line` mapping to `.piper` sources, constant checking and C# errors reported as internal compiler errors, larger sample programs, raw strings, C#'s numeric types and casts, `using`, equality on type parameters, `|` patterns that bind, the `piper` dotnet tool and the Piper.Build MSBuild package.
8. ~~**Async and iterators**~~, done after milestone 8: `async`/`await`, async lambdas and `Main`, `await for`, `await using`, `yield` iterators (sync and async), and the shared `Piper.Runtime` assembly.
9. ~~**Smaller features**~~, done: spreading sequences into collection literals (`[0, ...xs]`), `defer` and the pipe operator `|>`, generic trait methods, traits overriding inherited methods, `extend Trait`, and builtin, .NET and runtime types implementing traits (through witnesses).
10. ~~**A language server**~~, done: `piper lsp`, reusing the fault-tolerant parser and binder, with a VS Code extension in `editors/vscode`.

Version 1.0 includes everything above, and also:

- **Tooling:** debugging in VS Code (breakpoints and stepping in `.piper` files, through the C# extension's debugger), and signature help, inlay hints, semantic highlighting and an "add missing match arms" quick fix in the language server.
- **Patterns:** list patterns (`[first, ...rest]`), property patterns (`Point { x: 0 }`, `{ Length: > 5 }`) and relational patterns (`< 0`).
- **Attributes:** .NET attributes on declarations, fields and parameters, with `[Obsolete]` warnings for Piper declarations and .NET members.
- **Tests:** `test "name" { … }` blocks with `Assert` and `AssertEqual`, run by `piper test`.
- **Operators:** `operator +` and the other arithmetic operators on records and enums, as C# operators.
- **Preconditions and invariants:** `requires` clauses on functions, methods and records, checked at run time and, for constant arguments, at compile time.
- **`let … else`,** **named arguments and default values,** **doc comments** (`///`, shown in the editor and written as XML documentation) and **format specifiers** in interpolation (`{price:C}`, `{name,-10}`).

What comes next, including removing code only tests use from normal builds, is in [future.md](future.md).
