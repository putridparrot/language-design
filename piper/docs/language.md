# The Piper language (milestone 2)

Piper is a small, C-style, expression-oriented language that transpiles to C#. It borrows
immutable-by-default bindings, block expressions, enums (sum types) and exhaustive `match` from
Rust, `let`/`var` from Swift, and records and string interpolation from C#.

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
compilationUnit = { declaration } EOF ;
declaration     = function | record | enum ;

function        = "fn" IDENT "(" [ parameters ] ")" [ "->" type ] ( block | "=" expression ";" ) ;
record          = "record" IDENT "(" [ parameters ] ")" ";" ;
enum            = "enum" IDENT "{" [ variant { "," variant } [ "," ] ] "}" ;
variant         = IDENT [ "(" [ parameters ] ")" ] ;
parameters      = parameter { "," parameter } [ "," ] ;
parameter       = IDENT ":" type ;
type            = IDENT ;

block           = "{" { statement } [ expression ] "}" ;      (* final expression = block's value *)
statement       = variableDecl | while | for | return | "break" ";" | "continue" ";"
                | blockLike [ ";" ] | expression ";" | ";" ;
blockLike       = if | match | block ;                        (* ends at its '}', no ';' needed *)
variableDecl    = ( "let" | "var" ) IDENT [ ":" type ] "=" expression ";" ;
while           = "while" expression block ;
for             = "for" IDENT "in" expression block ;         (* expression must be a range *)
return          = "return" [ expression ] ";" ;

expression      = assignment ;
assignment      = binary [ ( "=" | "+=" | "-=" | "*=" | "/=" | "%=" ) assignment ] ;
binary          = unary { binaryOp unary } ;                  (* precedence table below *)
unary           = ( "-" | "!" ) unary | postfix ;
postfix         = primary { "(" [ arguments ] ")" | "." IDENT } ;
arguments       = expression { "," expression } [ "," ] ;
primary         = INT | DOUBLE | CHAR | STRING | "true" | "false" | IDENT
                | "(" expression ")" | block | if | match ;
if              = "if" expression block [ "else" ( block | if ) ] ;

match           = "match" expression "{" { arm ( "," | (* optional after a blockLike body *) ) } "}" ;
arm             = pattern [ "if" expression ] "=>" armBody ;
armBody         = expression | "return" [ expression ] | "break" | "continue" ;

pattern         = primaryPattern { "|" primaryPattern } ;
primaryPattern  = "_"                                         (* wildcard *)
                | IDENT                                       (* fieldless variant, else a binding *)
                | [ IDENT "." ] IDENT [ "(" [ pattern { "," pattern } [ "," ] ] ")" ]   (* variant / record *)
                | literalPattern [ ( ".." | "..=" ) literalPattern ] ;
literalPattern  = [ "-" ] ( INT | DOUBLE ) | CHAR | STRING | "true" | "false" ;
```

### Operator precedence (highest first)

| Level | Operators | Associativity |
|---|---|---|
| 8 | unary `-` `!` | prefix |
| 7 | `*` `/` `%` | left |
| 6 | `+` `-` | left |
| 5 | `<` `<=` `>` `>=` | left |
| 4 | `==` `!=` | left |
| 3 | `&&` | left |
| 2 | `\|\|` | left |
| 1 | `..` `..=` | left (only valid in `for`) |
| 0 | `=` `+=` `-=` `*=` `/=` `%=` | right |

The table lives in [SyntaxFacts.cs](../src/Piper.Compiler/Syntax/SyntaxFacts.cs); changing a
number there is all it takes to change precedence.

### Lexical structure

- **Identifiers:** ASCII letters, digits and `_`, not starting with a digit.
- **Keywords:** `fn let var if else while for in return break continue record enum match true false`.
- **Comments:** `// line` and `/* block */`. Block comments nest.
- **Integers:** `42`, `1_000_000`, `0xFF`, `0b1010`. Add an `L` suffix for Long: `3_000_000_000L`. An unsuffixed literal must fit in Int.
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

`print(value)` writes any value followed by a newline. Bools print as `true`/`false`, and output is
locale-independent. `main()` takes no parameters and returns Unit; it is the program's entry point.

## How Piper maps to C#

| Piper | C# |
|---|---|
| `record P(x: Int)` | `public sealed record P(int x);` |
| `enum Shape { Circle(r: Double), Empty }` | `public abstract record Shape` with a private constructor and nested `public sealed record Circle(double r) : Shape;`, `public sealed record Empty() : Shape;` |
| `Shape.Circle(1.0)` | `new Shape.Circle(1.0)` |
| `match s { Circle(r) if r > 1.0 => a, Empty => b, … }` | `if (s is Shape.Circle(var r) && r > 1.0) { … } else if … else { … }` |
| patterns `1 \| 2`, `1..=9`, `_` | C# patterns `1 or 2`, `>= 1 and <= 9`, `_` |
| `fn f(a: Int) -> Int { … }` | `public static int f(int a) { … }` in a static module class |
| `fn f(a: Int) -> Int = e;` | `public static int f(int a) => e;` |
| `let x = e;` / `var x = e;` | `int x = e;` (immutability is checked by Piper, not C#) |
| `if` expression, simple branches | `c ? a : b` |
| `if` expression, branches with statements | a variable assigned in each branch, or `return` in each branch |
| `for i in a..b` / `a..=b` | `for (int i = a; i < b; i++)` / `<=` (non-constant bounds hoisted) |
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

- Records and enum values print with C#'s `ToString`, so a Bool field shows as `True`.
- Integer overflow wraps silently, as in unchecked C#. `-2147483648` can't be written as a literal, because `2147483648` doesn't fit in Int.
- Match guards can't contain statements, and `|` alternatives can't bind variables.
- Overlapping literal and range patterns (`1..=5` followed by `3`) aren't reported as unreachable.
- There are no generic enums (such as `Option<T>`), lambdas, nullability, collections, modules or .NET interop yet. See the roadmap.

## Roadmap

1. ~~**Sum types and `match`**~~, done in milestone 2.
2. **Null safety and errors:** `T?`, `?.`, `??`, `if let` / `guard let`, `Option`/`Result`, and a `?` operator for propagating errors. Generic enums are a prerequisite, so this may follow item 3.
3. **Traits, generics and extensions:** traits become interfaces, generics take `where` constraints, and `extend` blocks become C# 14 extensions.
4. **Modules, imports and .NET interop.**
5. **Smaller features:** tuples and destructuring, lambdas, `defer`, `use`, async/await, and a pipe operator `|>`.
6. **A language server** that reuses the fault-tolerant parser and binder.
