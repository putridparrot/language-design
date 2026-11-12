# Tracker: notes from writing a real application in Piper

Tracker is an issue tracker API written in Piper to find out what writing a real program in it is like. This file
records each place where Piper got in the way, and what was done about it:
- **Fixed in the language:** only when the fix benefits Piper in general, not just this app.
- **Fixed in a plugin:** when the gap is the plugin's.
- **Recorded in [docs/future.md](../../docs/future.md):** when it's left for later.

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
| Dates from SQLite | Stored `DateTime`s come back without their UTC kind, as SQLite keeps them as text. | **App (pending):** use `DateTimeOffset`, or a UTC converter. That's EF Core with SQLite rather than Piper. |

The API also showed that Piper's interop handles ASP.NET Core's harder parts: a class deriving from the generic
`AuthenticationHandler<T>`, overriding its protected async method, reading `Request` bare as an inherited member.

What worked well:
- **Expression trees:** LINQ queries translated to SQL with no ceremony. Building a query in steps
  (`mut query = …; if status is Some(s) { query = query.Where(…) }`) reads naturally.
- **Errors as values:** `Result` with an `IssueError` enum kept them explicit and easy to test, and `let … else`
  handled the "not found" early return.
- **Workflow:** the state machine's actions changing the issue directly kept the workflow rules in one place.
