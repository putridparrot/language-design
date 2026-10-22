# The Piper language (milestone 1)

Piper is a small, C-style, expression-oriented language that transpiles to C#. It borrows
immutable-by-default bindings and block expressions from Rust, `let`/`var` from Swift, and
records and string interpolation from C#.

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
declaration     = function | record ;

function        = "fn" IDENT "(" [ parameters ] ")" [ "->" type ] ( block | "=" expression ";" ) ;
record          = "record" IDENT "(" [ parameters ] ")" ";" ;
parameters      = parameter { "," parameter } [ "," ] ;
parameter       = IDENT ":" type ;
type            = IDENT ;

block           = "{" { statement } [ expression ] "}" ;      (* final expression = block's value *)
statement       = variableDecl | while | for | return | "break" ";" | "continue" ";"
                | blockLike [ ";" ] | expression ";" | ";" ;
blockLike       = if | block ;                                (* ends at its '}', no ';' needed *)
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
                | "(" expression ")" | block | if ;
if              = "if" expression block [ "else" ( block | if ) ] ;
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
- **Keywords:** `fn let var if else while for in return break continue record true false`.
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

## Known limitations (milestone 1)

- Records print with C#'s `ToString`, so a Bool field shows as `True`.
- Integer overflow wraps silently, as in unchecked C#. `-2147483648` can't be written as a literal, because `2147483648` doesn't fit in Int.
- There are no lambdas, generics, enums/sum types, nullability, collections, modules or .NET interop yet. See the roadmap.

## Roadmap

1. **Sum types and `match`:** emitted as abstract records and switch expressions, with exhaustiveness checking.
2. **Null safety and errors:** `T?`, `?.`, `??`, `if let` / `guard let`, `Option`/`Result`, and a `?` operator for propagating errors.
3. **Traits, generics and extensions:** traits become interfaces, generics take `where` constraints, and `extend` blocks become C# 14 extensions.
4. **Modules, imports and .NET interop.**
5. **Smaller features:** tuples and destructuring, lambdas, `defer`, `use`, async/await, and a pipe operator `|>`.
6. **A language server** that reuses the fault-tolerant parser and binder.
