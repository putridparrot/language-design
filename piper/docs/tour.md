# A tour of Piper for C# developers

Piper compiles to C# and runs on .NET, and wherever the two could differ it does what C# does: the same types,
the same numeric rules, the same libraries. What it changes is the shape of the code. Everything is an
expression, values are immutable unless you say otherwise, there's no `null` (absence is a type), and
`match` checks that you've handled every case.

This tour takes about fifteen minutes. The full reference is [language.md](language.md).

## Running a program

Install the compiler as a .NET tool (it needs the .NET 10 SDK), then run a file:

```
dotnet tool install -g Piper --prerelease
piper run hello.piper
```

```piper
fn main() {
    print("Hello from Piper");
}
```

`piper build` writes the C#, and `piper emit` prints it, which is a good way to see what anything in this tour
turns into. To mix Piper into a C# project instead, see [Using Piper in a .NET project](#using-piper-in-a-net-project).

## Values and functions

```piper
let name = "Ada";            // immutable: C#'s `var`, but read-only
var count = 0;               // mutable
count += 1;

fn square(x: int) => int = x * x;            // one-expression function

fn greet(name: string, times: int) => string {
    let greeting = "Hello, {name}";           // every string interpolates: `{…}` is C#'s `$"{…}"`
    if times > 1 { "{greeting} x{times}" } else { greeting }
}
```

- Types are written after names, `name: Type`, and `=> Type` gives the return type.
- **Blocks and `if` are expressions.** A block's value is its last expression, written without `;`. That's why
  `greet` has no `return`, and why `if … { a } else { b }` replaces C#'s `?:`.
- The types are C#'s, with C#'s names: `int`, `long`, `double`, `decimal`, `byte`, `bool`, `string`,
  `char`, …. Conversions, overflow and casts (`(int)x`) follow C#'s rules exactly.
- `print` writes a value and a newline, using its C# `ToString`.

## Records and enums

```piper
record Point(x: double, y: double);           // a C# record: immutable, compared by value

enum Shape {                                   // a sum type: a value is exactly one of these
    Circle(center: Point, radius: double),
    Rect(corner: Point, width: double, height: double),
    Empty,
}

fn area(shape: Shape) => double = match shape {
    Circle(_, r) => 3.14159 * r * r,
    Rect(_, w, h) => w * h,
    Empty => 0.0,
};
```

Construct values by calling the type: `Point(1.0, 2.0)`, `Shape.Circle(origin, 2.0)` (C#'s `new` isn't needed).
`match` is exhaustive: leave out the `Empty` arm and the program doesn't compile, with an error naming the missing
case. Patterns nest, take guards (`Circle(_, r) if r > 10.0 => …`), match literals and ranges (`1..=9`), and
combine with `|`.

## No null: Option and Result

A `string` is never null. A value that might be missing has type `string?`, which is `Option<string>`: either
`Some(value)` or `None` (also written `null`).

```piper
fn find(names: List<string>, prefix: string) => string? {
    for name in names {
        if name.StartsWith(prefix) {
            return name;                       // a string converts to string? implicitly
        }
    }
    null
}

let found = find(names, "Gr");
print(found ?? "nobody");                      // ?? and ?. work as in C#
print(found?.Length ?? 0);
if found is Some(name) { print("found {name}"); }
```

Where .NET returns something that may be null (it's annotated), Piper gives you a `T?`, so you can't forget to
check. Errors that callers should handle are values too: `Result<T, E>` is `Ok(value)` or `Err(error)`, and `?`
passes an `Err` (or `None`) straight back to the caller:

```piper
fn parsePort(text: string) => Result<int, string> {
    if !int.TryParse(text, out let port) {     // out parameters work as in C#
        return Err("'{text}' is not a number");
    }
    if port < 1 || port > 65535 { Err("{port} is out of range") } else { Ok(port) }
}

fn connect(host: string, portText: string) => Result<string, string> {
    let port = parsePort(portText)?;           // returns the Err early, or unwraps the Ok
    Ok("{host}:{port}")
}
```

Exceptions are still there (`throw`, `try`/`catch`/`finally`) for crossing into .NET.

## Traits, generics and extensions

Traits are interfaces, and `extend` adds methods and trait implementations to any type, including ones you
didn't write:

```piper
trait Describe {
    fn describe() => string;
    fn shout() => string = this.describe().ToUpper();      // a default method
}

extend Point: Describe {
    fn describe() => string = "({this.x}, {this.y})";
}

extend int {
    fn isEven() => bool = this % 2 == 0;
}

fn describeAll<T: Describe>(items: List<T>) => string =
    string.Join(", ", items.Select(item => item.describe()));
```

Type arguments are inferred, and can be written as in C# when they can't be: `Enumerable.Empty<int>()`.

## Collections, lambdas and LINQ

```piper
using System.Linq;

let numbers = [5, 3, 8, 1];                    // a List<int>; [] makes an array where one is expected
let evens = numbers.Where(n => n % 2 == 0).Select(n => n * 10).ToList();
let squares: int[] = [1, 4, 9];
let grid = double[3, 3];                       // C#'s new double[3, 3]

let add = (a: int, b: int) => a + b;           // a Func<int, int, int>
let ages: Dictionary<string, int> = Dictionary();
ages["Ada"] = 36;
for (name, age) in ages { print("{name} is {age}"); }
```

Function types are written `(int, int) => int` and are .NET delegates, so lambdas go straight into LINQ and any
other .NET API.

## Async

As in C#: an `async fn` returns a `Task<T>` (or `Task`), and `await` waits for anything awaitable.

```piper
using System.Net.Http;
using System.Threading.Tasks;

async fn pageLength(client: HttpClient, url: string) => Task<int> {
    let body = await client.GetStringAsync(url);
    body.Length
}

async fn main() {
    using let client = HttpClient();
    print(await pageLength(client, "https://example.com"));
}
```

Async lambdas (`async x => await f(x)`), `await for` over an `IAsyncEnumerable` and `await using` work as their C#
counterparts do.

## Tuples

```piper
fn divide(a: int, b: int) => (quotient: int, remainder: int) = (a / b, a % b);

let (q, r) = divide(17, 5);                    // deconstruction, as C#'s var (q, r) = …
let result = divide(9, 2);
print(result.quotient);
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
print(builder.ToString());
```

## Modules

Each file can declare a `namespace`, and `private` hides a declaration from other files. A program is all of its
`.piper` files together: `piper run src/` compiles every file under `src`.

## Using Piper in a .NET project

Reference the `Piper.Build` package and a project compiles its `.piper` files along with its C#:

```xml
<ItemGroup>
  <PackageReference Include="Piper.Build" Version="1.0.0-preview.1" />
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
