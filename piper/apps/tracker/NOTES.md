# Tracker: notes from writing a real application in Piper

Tracker is an issue tracker API written in Piper to find out what writing a real program in it is like. This file
records each place where Piper got in the way, and what was done about it:
- **Fixed in the language:** only when the fix benefits Piper in general, not just this app.
- **Fixed in a plugin:** when the gap is the plugin's.
- **Recorded in [docs/future.md](../../docs/future.md):** when it's left for later.

## Running it

How to start, stop and use the API, and how it works, is in [README.md](README.md).

## Phase 1: domain (issues, workflow, validation)

| Gap | What happened | Fixed |
|---|---|---|
| A state machine can't start in a saved state | An issue loaded from the database must resume its workflow where it was, but a machine always started in its first state. Replaying every event would work, but is heavy for a status column. | **Plugin:** `Workflow.Resume(state, …)` makes a machine in a given state, without running `enter`. |
| An event given as data can't be checked | The API receives an event as data (`WorkflowEvent.Start("ada")`) and must refuse it if the status doesn't allow it, but only `CanStart(…)` existed, one per event. | **Plugin:** `CanFire(event)`. |
| An event's name | The error for a refused action needs the event's name. Piper enums have no `GetType()`, and no other way to get a variant's name. | **Plugin:** `event.Name` on the generated event enum. A general "name of a variant" is a language question, left open. |

## Phase 2: storage (EF Core, SQLite)

| Gap | What happened | Fixed |
|---|---|---|
| `let _ = f();` twice in a block | Discarding a result (`let _ = await AddIssue(…)`) twice in a test was "'_' is already declared". | **Language:** `let _ = …` is a discard, as `_` is in patterns: computed, never declared. |
| Piper enums as columns | A fieldless enum (`Priority`, `WorkflowState`) is a C# record hierarchy, which EF Core took for entities. | **Plugin (Piper.Data):** stored as the variant's name (`'High'`), optional ones as NULL. Making fieldless enums C# enums would help interop, but it's a large change, since enums have methods, operators and traits. |
| `let` fields as columns | `public let created` is a get-only property, which EF Core doesn't map, so it couldn't bind `Issue`'s constructor. | **Plugin (Piper.Data):** get-only auto-properties are mapped through their backing fields. Computed properties aren't. |

| Listing, parsing and naming enum variants | Filtering by `?status=InProgress` needed the variant named by a string. A fieldless enum is a record hierarchy, so `Enum.Parse` and `Enum.GetValues` don't apply, and Piper had nothing in their place. | **Language:** every enum has `value.Name`, and a fieldless one has `E.Values` and `E.Parse(text) => E?`, written as an `extend` block for each enum. This replaced the state-machines plugin's own `Name`. Two follow-on fixes: a variant's JSON and printing show only its fields, not members every variant inherits. |
| A nullable task's result | `await db.Issues.FirstOrDefaultAsync(…)` gave `Some(null)` for a missing row: .NET's `Task<T?>` wasn't treated as `T?` is. | **Language:** a .NET call returning `Task<T?>` or `ValueTask<T?>` is a task of an Option, so awaiting it gives None for null. `samples/todo-api` had the same hidden null, with `GetFromJsonAsync`. |

## Phase 3: the API

| Gap | What happened | Fixed |
|---|---|---|
| Interpolated raw strings | JSON request bodies in tests, `"""{"title": "{{title}}"}"""`, couldn't interpolate. | **Language:** `$"""…{x}…"""` and `$$"""…{{x}}…"""`, as in C#. |
| Policies from configuration | The search limit belongs in settings, but a `policies` block became a function of `services` alone. | **Plugin (web):** `policies Security(settings: Settings) { ratelimit "search" fixed settings.searchesPerMinute per minute; }`. |
| An error field named `status` | `NotAllowed(action, status)` lost `status` in its ProblemDetails, by design, as it would clash with ProblemDetails' own. That's documented, but easy to walk into. | **App:** renamed it `current`. A warning from the web plugin for such fields is worth adding (future). |
| Combining `Ok`/`Err` arms | `match r { Ok(v) => Ok(f(v)), Err(e) => Err(e) }` with no expected type can't infer `Result<T, E>`, since each arm knows half. | **Not needed here:** `?>` maps an `Ok` (or `Some`) and passes errors through, and `?` returns them. Unifying the arms would still be nicer; it's recorded. |
| Dates from SQLite | Stored `DateTime`s come back without their UTC kind, as SQLite keeps them as text. | **App:** the tracker stores UTC, so its views mark what they read as UTC (`Utc(…)`). That's EF Core with SQLite rather than Piper, and Piper.Data can't know a program's convention. |

## Phase 4: operations (notifications, the command line)

| Gap | What happened | Fixed |
|---|---|---|
| Assigning a .NET static property | `Environment.ExitCode = code` was refused with a misleading "record fields are immutable": .NET static properties and fields couldn't be assigned. | **Language:** they can, as in C#. One without a public setter is reported as such. |
| A function hiding a type | A command function named `List` made every `List<T>` in the namespace an error: "'List' is a function, not a type". | **Language:** a type position looks past functions and variables of the name to a type, as C# does. |

What worked well:
- **`[Retry(3, 50)]` on the webhook notifier:** it read as intended. A fake `HttpMessageHandler` written in Piper
  (overriding the protected-internal `SendAsync`) showed it trying three times.
- **Admin commands:** the `args` declaration gave usage and errors for free, and list patterns on its words
  (`["import", code, file] => …`) made the command dispatch read like a table.
- **The new enum members** paid off at once: `Priority.Parse` for the CSV import, `Priority.Values` in its error message,
  and `issue.status.Name` in listings.

## Phase 5: a desktop client (Avalonia)

[apps/tracker-desktop](../tracker-desktop): a client for the API ([client/](../tracker-desktop/client)), a view model
using the `aspects` plugin's `[Notify]` and `[Command]` ([viewmodels/](../tracker-desktop/viewmodels)), and a window
laid out in code, as Piper has no XAML ([views/](../tracker-desktop/views)). It lists a project's issues by status and
title, moves them through the workflow, adds them with a description, and shows and adds their comments. Project
creation, an admin's job, stays with the command line.

| Gap | What happened | Fixed |
|---|---|---|
| Inherited .NET statics | `ComboBox.SelectedIndexProperty` was "no member": it's declared by `ComboBox`'s base, `SelectingItemsControl`. Static methods were found through a derived type, but static properties and fields weren't. | **Language:** they are, as in C#, nearest declaration first. |
| `(_, _) =>` | An event handler ignoring both its arguments was "'_' is already declared". | **Language:** a `_` lambda parameter binds nothing, as in C#. |
| `Deserialize<T>` through a type parameter | A generic `Send<T>` returning `JsonSerializer.Deserialize<T>(…)` couldn't treat the result as optional: .NET's `T?` on an unconstrained `T` is null for a reference type, but Piper reads it as plain `T`, so a JSON `null` would slip through as a null `T`. | **Fixed in the language** (recorded at first, with a workaround: `Send` returned the body, and each call deserialized to its own type). The result is now a `T?`, so the app's `Send<T>` deserializes, and a JSON `null` is an error. |
| Async event handlers | `model.PropertyChanged += async (_, e) => { await … }` is an error: an async lambda must return a Task, but the handler returns nothing. C# allows it (`async void`). | **Fixed in the language** (recorded at first, with the handler running the `[Command]` instead). An event's handler can now be async, as C#'s `async void`, and only there. The app keeps running the commands, as they report failures in its status line through `OnCommandFailed`. |
| A command's exception lost | An async `[Command]` was started and its task dropped, so anything it threw (a bug, or a failure the client didn't turn into a `Result`) vanished: the button did nothing, and nothing said why. | **Plugin:** an `OnCommandFailed(command, error)` method on the class hears of each command's failure; the app shows it in the status line. Without one, an async command's exception is thrown where C#'s `async void` would throw it, on the UI thread, rather than lost. |
| `try` as a value | `try { …; Ok(x) } catch (e: …) { Err(…) }` as a function's last expression isn't its value: `try` is a statement, as in C#, and the function "doesn't return on all paths". | **App:** `return` in each branch. Not a gap: it's C#'s rule, and `match` on a `Result` is the Piper way. |
| A list control and `ObservableCollection` | Refreshing the issues with `Clear()` and `Add` lost the chosen issue (the ListBox cleared its selection after the view model set it) and left the old row drawn under the new. | **App:** each refresh replaces the list (`prop Issues: List<IssueView>`), and the list uses a plain `StackPanel`. That's Avalonia's behaviour, not Piper's. |

Also found:
- **`[STAThread]` wasn't needed:** [docs/future.md](../../docs/future.md) expected Avalonia to need it on `Main`, which
  Piper's generated entry point doesn't carry. Avalonia ran without it, clipboard and drag-and-drop aside (WPF and
  WinForms still need it).
- **A console window opens** beside the app when it's built with `--exe`, as there's still no `WinExe` setting.

What worked well:
- **Object initializers** made the control tree read like XAML: `Button() { Content: "Add", Command: model.AddIssueCommand }`,
  with nested `Children: { … }`.
- **`[Notify]` and `[Command]`:** buttons enabled and disabled themselves from computed properties
  (`prop CanSubmit => bool = …`) with nothing written by hand, and the view model was testable without a window.
- **`Result` and `?`** made the client's calls short: `let content = (await Send(…))?;` then deserialize.

## Summary

Nineteen gaps, each found by writing an ordinary program. The API and command line (phases 1–4) found fourteen:
- **Language (7):** enum `Name`/`Values`/`Parse`, interpolated raw strings, `let _` discards, nullable tasks,
  assigning .NET statics, types not hidden by functions, and variant JSON and printing.
- **Plugins (6):** `Resume` and `CanFire` for state machines, enums and `let` fields as columns, and `policies` with
  parameters.
- **Recorded for later, then dealt with:**
  - **`Ok`/`Err` arms:** now inferred together. `match r { Ok(v) => Ok(…), Err(e) => Err(e) }`, and `if … { Some(…) }
    else { None }`, say their type between them (language).
  - **An error field named `status`:** now a warning from the web plugin, at the field.
  - **Fieldless enums as C# enums:** decided against. Every enum has members (`Name`, `Values`, `Parse`, extend
    blocks) a C# enum can't hold, and enums with traits or operators couldn't be C# enums at all, so there would be two
    representations. Patterns, construction, nullability (a C# enum is a value type) and the JSON, EF and web
    boundaries would all change. What it would buy, columns and JSON names, the converters already give.

None of those needed a workaround in the app that a newcomer would have had to discover. Each fix is general, and is
covered by tests outside the app as well.

The desktop client (phase 5) found five more:
- **Language (2):** .NET statics through a derived type, and `_` lambda parameters.
- **Plugin (1):** `[Command]` failures reported, through `OnCommandFailed`, or thrown as `async void`'s are.
- **Recorded for later (2):** .NET's `T?` through a Piper type parameter, which lets a null in, and async event
  handlers. Unlike the API's gaps, these needed workarounds a newcomer would have to find: deserializing in each call
  rather than one generic one, and running a command from an event handler rather than awaiting there. Both have
  since been fixed in the language. The first's workaround is removed (`Send<T>` deserializes for every call); the
  second's is kept on its merits, as the commands report their failures.

Two more problems weren't Piper's: `try` is a statement, as in C#, and Avalonia's list control loses its selection
when its items are reset.

The API also showed that Piper's interop handles ASP.NET Core's harder parts: a class deriving from the generic
`AuthenticationHandler<T>`, overriding its protected async method, reading `Request` bare as an inherited member.

What worked well:
- **Expression trees:** LINQ queries translated to SQL with no ceremony. Building a query in steps
  (`mut query = …; if status is Some(s) { query = query.Where(…) }`) reads naturally.
- **Errors as values:** `Result` with an `IssueError` enum kept them explicit and easy to test, and `let … else`
  handled the "not found" early return.
- **Workflow:** the state machine's actions changing the issue directly kept the workflow rules in one place.
