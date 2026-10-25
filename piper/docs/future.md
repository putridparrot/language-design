# Future requirements

Work planned for after 1.0. Under the compatibility promise every item must be an addition: no 1.x release may stop a
program compiling or change what it means.

## Builds

### Remove code only tests use

A normal build leaves out `test` blocks, but compiles everything they call. Functions, records, enums, traits and
methods used only by tests still end up in the binary.

**Requirement:** a normal build (`piper build`, `piper run`, Piper.Build) leaves out every declaration reachable only
from tests. The binary should hold the bare minimum the program needs.

- **Roots:** what the program can reach without tests: `Main`, every public declaration when building a library (C# and
  other assemblies may call it), and whatever .NET reaches by reflection or attributes (see below).
- **Reachability:** follow calls, constructions, types named in signatures and fields, trait implementations
  (a trait method is reachable if the trait is and the type is), operators, extend blocks, and functions used as values.
- **Scope:** private and test-only declarations in an executable can go. In a library, only declarations private to a
  file and unreachable from its public ones can, since anything public may be called from outside.
- **Unsafe cases:** code .NET finds by reflection (serializers reading properties, `[ModuleInitializer]`, attribute-driven
  frameworks) must stay. Removing whole declarations, not record fields or properties, avoids most of this.
- **Checking:** `piper build --exe` on a program with test-only helpers produces an assembly without them. The output of
  every sample and golden program is unchanged, and the binary is smaller or the same.
- **Later:** extend the same analysis to remove anything unreachable, test-only or not, behind an option, since a
  library's public API must stay intact.

### References in a Piper.Build project

`piper.json`'s `"packages"`, `"frameworks"` and `"references"` work for a program folder. In a project using Piper.Build,
the `.piper` files are still only checked against .NET's own assemblies, not the project's `PackageReference`s. The
build should pass the project's resolved references (`@(ReferencePath)`) to the compiler, so Piper code there can use
them too.

### Functions as values for other delegate types

A function passed by name is a `Func` or `Action`, so `numbers.ConvertAll(Square)` reports no matching overload: `List<T>.ConvertAll` takes a
`Converter<T, TOutput>`. A function, like a lambda, should convert to any delegate type it fits, including one a generic
method's type arguments are inferred for.

### .NET generics instantiated with Piper traits

`services.AddSingleton<Clock>(SystemClock())`, where `Clock` is a Piper trait and `SystemClock` a record implementing it
with `extend`, reports no matching overload. A Piper type should convert to a trait it implements when the trait is a
.NET method's type argument.

## Language

- **More for classes:**
  - `base.F()`, to call the method an override replaces.
  - Type tests on classes in `match` and `is` (`match shape { c: Circle => … }`), and casts down to a derived class.
  - Properties with getters and setters, which would also let a class complete a .NET base with abstract properties
    (`Stream`), and `protected` members.
  - Constructors besides the primary one, and attributes on fields.
  - Bare calls to a trait's own methods in its defaults (`Name()` rather than `this.Name()`), as classes allow.
- **Custom `ToString` on records:** today `ToString` is reserved on records, so a record can't choose how it prints.
- **Comparison operators** on records and enums (`<`, `<=`, `>`, `>=`), generating the pairs C# requires.
- **`const` declarations,** usable in patterns and attribute arguments (`const` is reserved).
- **.NET implicit conversions** (`op_Implicit`), so `BigInteger.Pow(2, 70)` works without writing `BigInteger(2)`.
- **Namespace-qualified variants in patterns** (`Library.Loans.LoanError.UnknownBook(code)`).
- **Attributes on enum variants,** and explicit targets (`[return: …]`).
- **More contracts:** `requires` on trait methods (implementations must not demand more than the trait does), invariants on
  enum variants, and postconditions (`ensures result >= 0`).
- **Obsolete warnings for setting obsolete .NET properties and subscribing to obsolete events** (reading and calling are
  already reported).

## Tools

- **`piper fmt`:** a formatter.
- **`piper new`:** creates a program folder with a `piper.json` and a starter `Main`.
- **Language server:**
  - `piper.json` errors while typing, not just on save;
  - a quick fix to add a missing `using`;
  - signature help inside lambdas.
