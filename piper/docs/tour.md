# A tour of Piper for C# developers

Piper compiles to C# and runs on .NET, and wherever the two could differ it does what C# does: the same types,
the same numeric rules, the same libraries. What it changes is the shape of the code. Everything is an
expression, values are immutable unless you say otherwise, there's no `null` (absence is a type), and
`match` checks that you've handled every case.

This tour takes about fifteen minutes. The full reference is [language.md](language.md).

## Running a program

Install the compiler as a .NET tool (it needs the .NET 10 SDK), then run a file:

```
dotnet tool install -g Piper
piper run hello.piper
```

```piper
fn Main() {
    Print("Hello from Piper");
}
```

`piper build app.piper --exe` compiles it to an executable (`bin/app.exe`), and `--library` to a DLL. Plain
`piper build` writes the C#, and `piper emit` prints it, which is a good way to see what anything in this tour turns into. To mix Piper into a C# project instead, see [Using Piper in a .NET project](#using-piper-in-a-net-project).

## Values and functions

```piper
let name = "Ada";            // immutable: C#'s `var`, but read-only
mut count = 0;               // mutable
count += 1;

fn Square(x: int) => int = x * x;            // one-expression function

fn Greet(name: string, times: int) => string {
    let greeting = "Hello, {name}";           // every string interpolates: `{…}` is C#'s `$"{…}"`
    if times > 1 { "{greeting} x{times}" } else { greeting }
}
```

- Types are written after names, `name: Type`, and `=> Type` gives the return type.
- **Blocks and `if` are expressions.** A block's value is its last expression, written without `;`. That's why
  `Greet` has no `return`, and why `if … { a } else { b }` replaces C#'s `?:`.
- The types are C#'s, with C#'s names: `int`, `long`, `double`, `decimal`, `byte`, `bool`, `string`,
  `char`, …. Conversions, overflow and casts (`(int)x`) follow C#'s rules exactly.
- `Print` writes a value and a newline, using its C# `ToString`.

## Records and enums

```piper
record Point(x: double, y: double);           // a C# record: immutable, compared by value

enum Shape {                                   // a sum type: a value is exactly one of these
    Circle(center: Point, radius: double),
    Rect(corner: Point, width: double, height: double),
    Empty,
}

fn Area(shape: Shape) => double = match shape {
    Circle(_, r) => 3.14159 * r * r,
    Rect(_, w, h) => w * h,
    Empty => 0.0,
};
```

Construct values by calling the type: `Point(1.0, 2.0)`, `Shape.Circle(origin, 2.0)` (C#'s `new` isn't needed).
Records are immutable; to change a field, copy with a spread, as in TypeScript: `{ ...p, y: 5.0 }`.
`match` is exhaustive: leave out the `Empty` arm and the program doesn't compile, with an error naming the missing
case. Patterns nest, take guards (`Circle(_, r) if r > 10.0 => …`), match literals, ranges (`1..9` includes 9, as in Swift; `1..<9` stops at 8) and comparisons
(`< 0`), and combine with `|`. As in C#, there are list patterns, `[first, ...rest]` (Piper writes C#'s `..` as `...`,
as in its spreads), and property patterns, `Point { x: 0 }` or `{ Length: > 5 }`.

## No null: Option and Result

A `string` is never null. A value that might be missing has type `string?`, which is `Option<string>`: either
`Some(value)` or `None` (also written `null`).

```piper
fn Find(names: List<string>, prefix: string) => string? {
    for name in names {
        if name.StartsWith(prefix) {
            return name;                       // a string converts to string? implicitly
        }
    }
    null
}

let found = Find(names, "Gr");
Print(found ?? "nobody");                      // ?? and ?. work as in C#
Print(found?.Length ?? 0);
if found is Some(name) { Print("found {name}"); }
```

Where .NET returns something that may be null (it's annotated), Piper gives you a `T?`, so you can't forget to
check. Errors that callers should handle are values too: `Result<T, E>` is `Ok(value)` or `Err(error)`, and `?`
passes an `Err` (or `None`) straight back to the caller:

```piper
fn ParsePort(text: string) => Result<int, string> {
    if !int.TryParse(text, out let port) {     // out parameters work as in C#
        return Err("'{text}' is not a number");
    }
    if port < 1 || port > 65535 { Err("{port} is out of range") } else { Ok(port) }
}

fn Connect(host: string, portText: string) => Result<string, string> {
    let port = ParsePort(portText)?;           // returns the Err early, or unwraps the Ok
    Ok("{host}:{port}")
}
```

Exceptions are still there (`throw`, `try`/`catch`/`finally`) for crossing into .NET.
`defer` runs cleanup code when a block ends, however it ends: `defer file.Close();`.

## Traits, generics and extensions

Traits are interfaces, and `extend` adds methods and trait implementations to any type, including ones you
didn't write:

```piper
trait Describe {
    fn Describe() => string;
    fn Shout() => string = this.Describe().ToUpper();      // a default method
}

extend Point: Describe {
    fn Describe() => string = "({this.x}, {this.y})";
}

extend int {
    fn IsEven() => bool = this % 2 == 0;
}

fn DescribeAll<T: Describe>(items: List<T>) => string =
    string.Join(", ", items.Select(item => item.Describe()));
```

Type arguments are inferred, and can be written as in C# when they can't be: `Enumerable.Empty<int>()`.

Records and enums can have arithmetic operators, declared in an extend block with `this` as the left operand:

```piper
extend Point {
    operator +(other: Point) => Point = Point(this.x + other.x, this.y + other.y);
}

let moved = Point(1.0, 2.0) + Point(0.5, 0.5);
```

## Collections, lambdas and LINQ

```piper
using System.Linq;

let numbers = [5, 3, 8, 1];                    // a List<int>; [] makes an array where one is expected
let evens = numbers.Where(n => n % 2 == 0).Select(n => n * 10).ToList();
let squares: int[] = [1, 4, 9];
let more = [0, ...numbers, ...squares];        // spread sequences in, as C#'s [0, ..numbers]
let total = numbers |> Enumerable.Sum();      // x |> F(a) is F(x, a)
let grid = double[3, 3];                       // C#'s new double[3, 3]

let add = (a: int, b: int) => a + b;           // a Func<int, int, int>
let ages: Dictionary<string, int> = Dictionary();
ages["Ada"] = 36;
for (name, age) in ages { Print("{name} is {age}"); }
```

Function types are written `(int, int) => int` and are .NET delegates, so lambdas go straight into LINQ and any
other .NET API.

## Async

As in C#: an `async fn` returns a `Task<T>` (or `Task`), and `await` waits for anything awaitable.

```piper
using System.Net.Http;
using System.Threading.Tasks;

async fn PageLength(client: HttpClient, url: string) => Task<int> {
    let body = await client.GetStringAsync(url);
    body.Length
}

async fn Main() {
    using let client = HttpClient();
    Print(await PageLength(client, "https://example.com"));
}
```

Async lambdas (`async x => await F(x)`), `await for` over an `IAsyncEnumerable` and `await using` work as their C#
counterparts do. So do iterators: a function that `yield return`s values returns them lazily as an `IEnumerable<T>`:

```piper
fn Evens() => IEnumerable<int> {
    mut n = 0;
    while true {
        yield return n;
        n += 2;
    }
}

Print(string.Join(", ", Evens().Take(5)));   // 0, 2, 4, 6, 8
```

## Tuples

```piper
fn Divide(a: int, b: int) => (quotient: int, remainder: int) = (a / b, a % b);

let (q, r) = Divide(17, 5);                    // deconstruction, as C#'s var (q, r) = …
let result = Divide(9, 2);
Print(result.quotient);
```

## Calling .NET

All of .NET is available. Import namespaces with `using`, and call types and members as you would in C#:

```piper
using System;
using System.IO;
using System.Text;

let builder = StringBuilder();
builder.Append("lines: ");
using let reader = File.OpenText("notes.txt");   // disposed at the end of the block
while reader.ReadLine() is Some(line) {
    builder.Append(line.Length).Append(' ');
}
Print(builder.ToString());
```

Attributes work as in C#, on declarations, fields and parameters. A record field's attribute goes on the property, so
serializers see it:

```piper
using System.Text.Json;
using System.Text.Json.Serialization;

record Person([JsonPropertyName("first_name")] first: string, age: int);

Print(JsonSerializer.Serialize(Person("Ada", 36)));   // {"first_name":"Ada","age":36}
```

## Testing

Write tests next to the code, as `test` blocks, and run them with `piper test`:

```piper
fn Divide(a: int, b: int) => (quotient: int, remainder: int) = (a / b, a % b);

test "divides with a remainder" {
    let (q, r) = Divide(17, 5);
    AssertEqual(3, q);
    Assert(r == 2);
}
```

```
$ piper test src/
PASS divides with a remainder

1 passed, 0 failed
```

A failing `Assert` reports its condition (`Assertion failed: r == 2`) and the line. Tests are checked in every build
but only compiled in by `piper test`.

## Modules

Each file can declare a `namespace`, and `private` hides a declaration from other files. A program is all of its
`.piper` files together: `piper run src/` compiles every file under `src`.

## Using Piper in a .NET project

Reference the `Piper.Build` package and a project compiles its `.piper` files along with its C#:

```xml
<ItemGroup>
  <PackageReference Include="Piper.Build" Version="1.0.0" />
</ItemGroup>
```

It also brings in `Piper.Runtime`, the small library that defines `Option` and `Result` for all Piper code. (C# from`npiper build` needs that package too.) Piper functions are static methods of a class named after their file (`geometry.piper` → `Geometry`), and records,
enums and traits are ordinary C# types, so C# code can call Piper code directly. Errors appear in the build output
against `.piper` lines, and stack traces and the debugger show Piper source.

## Where next

- [language.md](language.md): the full reference, including exactly how each construct maps to C#.
- Larger example programs: a [word counter](../tests/Piper.Tests/Golden/program_word_count.piper), a
  [calculator](../tests/Piper.Tests/Golden/program_calculator.piper), [Game of Life](../tests/Piper.Tests/Golden/program_life.piper)
  and a multi-file [inventory](../tests/Piper.Tests/Golden/program_inventory/). Each has its generated C# beside it.
