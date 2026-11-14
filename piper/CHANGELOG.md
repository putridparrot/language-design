# Changelog

## Unreleased

- **Testing web apps in memory (web plugin):** `using let site = await TestClient.Start(() => BuildApp());` serves the
  app its build function makes from memory, with no port, and is an `HttpClient` calling it: it keeps cookies,
  `PostForm(url, fields, htmx: true)` sends a form's anti-forgery token for it, responses stream (so live pages'
  events can be read), and `site.App` is what the function returned. The server is Piper.Web's own, swapped in while
  the app is built (as WebApplicationFactory does), so the app's code is unchanged and no package is added.
  apps/auction's and apps/tracker's HTTP tests use it.
- **Fixed (web plugin):** a request ASP.NET Core couldn't bind (a missing body, say), to a handler returning an
  Option, a Result or markup, was a 500 from Piper.Web's response filter rather than ASP.NET Core's 400.
- **Generators written in Piper:** a property test's `in` takes a `PiperGen<T>`, made with `PiperGen.For<T>()`,
  `Range`, `In`, `Constant`, `OneOf`, `ListOf` and `Shuffled`, and changed with `Select`, `Where` and `SelectMany`:
  `fn Evens() => PiperGen<int> = PiperGen.For<int>().Where(n => n % 2 == 0);`. Their values shrink as any do (a
  shuffle towards the order given). apps/grid's order test uses `Shuffled`.
- **Editor support for markup:** typing a start tag's `>` closes the element after the cursor (the language server's
  `piper/autoClose`, which the VS Code extension asks; `piper.autoClosingTags` turns it off), and hover on HTML's
  elements and attributes, and htmx's, says what each is for, with a link to MDN or htmx's site.
- **Newtypes compare as their value:** one whose value can be compared (numbers, strings, chars, and .NET types
  implementing `IComparable<T>`) has `<`, `<=`, `>` and `>=`: `newtype Money = decimal;` gives `price < limit`.
- **A newtype in a form (web plugin)** is read as its value and then made, so its `requires … else "…"` message is
  the field's error, rather than "'0' isn't a money". apps/auction's prices and bids are now `Money`, always more than
  nothing.
- **`if` without `else` in markup takes any hole's value:** `{if bids > 1 { $"{bids} bids" }}` and
  `{if hot { count }}` work, as text and numbers do in holes, rather than only markup.
- **Property tests:** `test "name" for (xs: List<int>, n: int in 1..100, c: char in "aeiou") requires … { … }` runs
  its body over 100 sets of generated values, and when it fails, shrinks them to the smallest that still fail, shown
  with the seed that found them: `piper test --seed <n>` repeats it. Values are made from each parameter's type
  (numbers, text, collections, Options, Results, tuples, and the program's records, newtypes and enums, a record's
  `requires` refusing its own), or from an `in` range or collection; the test's `requires` throws away the cases it
  refuses. Generating and shrinking are Piper.Runtime's own (`PiperGen`, `PiperProperty`), with no other library:
  shrinking works on the random choices the values were made from, as Hypothesis does, so every type shrinks alike. A
  range of a type that isn't an integer or char is an error (PIP0399), as is one missing an end (PIP0398).
  apps/auction tests that bids never fall, whatever bids come in, and apps/grid that a job's figures don't depend on
  the order its chunks come back in.
- **Editor support for markup:** the VS Code grammar reads markup as markup (tags, attributes, text and entities, with
  holes as Piper, and `<script>`/`<style>` as text), where it coloured it as code. In the language server, a
  component tag is its function: coloured, hovered and gone to, and renamed in both its tags (the lexer now keeps where
  a closing tag names its element). An error in a component's attribute is reported at the attribute. Completion in
  markup offers tags (HTML's elements and the components), attributes (a component's parameters, HTML's and htmx's),
  and some values: the record's fields for an input's `name` inside `<form for={NewLot}>`, an input's `type`, a form's
  `method`, `hx-swap`. Text offers nothing, and a hole is code, as elsewhere.
- **Checked field names (web plugin):** `FormErrors` is now `FormErrors<T>`, and a constant field name given to
  `errors.For("…")` or `form.Text("…")` must be one of the record's fields (PIP0511). Any .NET method can ask for the
  check by marking a string parameter `[PiperFieldName]` (Piper.Runtime). Programs naming the type write
  `FormErrors<NewLot>` instead of `FormErrors`.
- **Functions in an `actor`** (both actor plugins): `fn` and `async fn` beside its state and handlers, for what its
  handlers share; they can change its state, and run only from its handlers.
- **Query values in route functions (web plugin):** a handler's parameters outside its route, of a simple type or
  optional, are parameters of its route function too, after the route's: `SiteRoutes.Search(q: "clock", page: 2)` is
  `/search?q=clock&page=2`, and optional ones default to None and are left out. A required one of the program's own
  type isn't seen, as the plugin judges by the type as written.

- **A form's every mistake at once (web plugin):** each of a record's `requires` is checked wherever the fields it
  mentions were read, so a field that isn't a number no longer hides what's wrong with the others, and several failed
  conditions are all reported. For it, the compiler gives each record with `requires` a static `__PiperRequirements`
  table (Piper.Runtime's `PiperRequirement`): the fields each condition mentions, and a check giving its message.
  `FormErrors.Form` puts several form-wide messages together. A `requires` without `else` that mentions one field is
  now that field's error, as one with `else` was.
- **Typed forms:** `<form for={NewLot}>` checks the name of each `input`, `select` and `textarea` written in it against
  the record's fields (PIP0510, listing them); `for` isn't written out. apps/auction's forms use it.
- **List literals type `None`s from their other elements:** `[None, None, Some("ada")]` is a `List<string?>`, `[[1], []]`
  a `List<List<int>>`; `null`, `Ok(…)`, `Err(…)` and untyped lambdas likewise, with no annotation.
- **`[return: NotNullIfNotNull]`:** `WebUtility.HtmlDecode(text)`, `Path.GetFileName(path)` and the like give a plain
  value for a plain argument, rather than an Option. Code that wrote `?? fallback` after such a call is now an error
  (PIP0261, as `??` needs an optional), and can drop it: the samples' `interop.piper` did.
- **Functions in a `statemachine`:** `private fn Take(…) { … }` beside the machine's data and states, for what its
  transitions share; they're its class's methods. apps/auction's `LotLife` takes a bid in one.

- **apps/auction:** an auction site proving Piper's web UI: markup pages, typed links, forms read into records with
  `requires` messages, htmx, and live pages (each bid, and each lot's countdown, pushed to every page watching it). Each
  lot is an actor taking its bids one at a time, and its life a state machine. Tried in a browser as well as tested.
  From it:
  - **Actors' ask handlers infer from their declared result** (both actor plugins): `Ok(view)`, `Err(…)` and `None` in
    a handler's body take their type from its `=> Result<…>`, as in a function.

- **Markup:** HTML in expressions, as JSX has it, of a new builtin type `Html`: `<td><a href={url}>{title}</a></td>`.
  Holes are escaped by their type (a string is text, an `Html` is markup, a number takes a format, `{price:C}`), and
  what can't go in markup (a Task, a bool, a function) is an error saying so. Components are functions used as tags
  (`<Badge text="x" />`, children as a `children` argument); `{for x in xs { … }}` and `if` without `else` work in
  holes; `name={flag}` is a boolean attribute; void elements, `<script>` and `<style>` and `<!doctype html>` are HTML's.
  It compiles to calls to Piper.Runtime's `PiperHtml`, and a web handler returning `Html` answers with a page.
- **Live pages (web plugin):** `Live.Publish("lot-7", "price", fragment)` pushes markup to every page watching the
  topic, which htmx's server-sent events extension (htmx-ext-sse 2.2.2, Zero-Clause BSD, loaded by `<Htmx.Script />`)
  swaps in: `<section hx-ext="sse" sse-connect={Live.Url("lot-7")}><span sse-swap="price">…</span></section>`. Apps
  serve the connections at `/_piper/live`, with keep-alives, and drop a slow page's oldest updates rather than wait.
  Checked in a browser (headless Edge): htmx's swaps and pushed updates both arrive.
- **htmx (web plugin):** apps serve htmx 2.0.4 (Zero-Clause BSD, in Piper.Web) at `/_piper/htmx.min.js`, which
  `<Htmx.Script />` loads with the anti-forgery token sent as a header on htmx's requests. `endpoints Site layout Page`
  puts each endpoint's markup in `Page(body)` for a browser, and sends the fragment alone when htmx asks. An
  `HtmxRequest` handler parameter reads htmx's request headers and sets its response ones (`Redirect`, `PushUrl`,
  `TriggerEvent`, `Retarget`, …).
- **`[NotNullWhen(true)] out T?`** (`Version.TryParse`, `HttpHeaders.TryGetValues`) gives a plain value where the call
  returned true, as `[MaybeNullWhen(false)]` already did.
- **Messages for `requires`:** `requires title.Trim().Length > 0 else "Give the lot a title."` fails with that message
  (any string, built only on failure), as a `PiperContractException` naming the field or parameter the condition is
  about when it mentions just one. Forms show it beside that field, JSON reports it, and a constant call breaking it
  is reported with it. Conditions without `else` are unchanged.
- **Typed links and forms (web plugin):** a named endpoint (`get Lot "/lots/{id:int}" (id: LotId) => …`) has a route
  function, `SiteRoutes.Lot(id: LotId) => string`, so links are checked against the routes and their types. A handler
  taking a `Form<T>` gets a posted form read into a record: `form.Result` is `Ok(record)`, or `Err(errors)` with a
  message per field (missing, or not a value of its type) and for a failed `requires`. Every `<form method="post">` in
  markup carries an anti-forgery token, which `Form<T>` checks.
- **Piper.Runtime's `Option` and `Result` from .NET are Piper's:** a library member returning them (such as
  `form.Result`) is matched with `Some`/`None` and `Ok`/`Err`, as Piper's own are.
- **Generic .NET methods' constraints are checked:** an overload whose inferred type arguments break its constraints
  (`where T : IFormattable` for an Option) is passed over, as C# does, rather than chosen and then rejected by C# as an
  internal compiler error.

- **`piper run --watch` reloads the browser:** after a restart, a web program's open pages reload once it answers
  again. Each watched run of a program using ASP.NET Core loads Piper.Watch (beside piper), an ASP.NET Core hosting
  startup putting a small script in the HTML pages a browser asks for, which listens to an event stream the watcher
  serves on loopback. Only pages are touched: JSON, files and event streams the program serves pass through unbuffered.

- **Newtypes:** `newtype IssueId = int;` makes a distinct type over a value, which can't be mixed up with its type or
  another newtype over it (`FindIssue(ProjectId(5))` is an error). It's a record struct with one field, `value`, so it
  costs nothing at run time, and takes `requires` (`newtype Email = string requires value.Contains("@");`). It stands for
  its value: it prints as it, formats and sorts as it, is it in JSON, and has `IssueId.Parse(text) => IssueId?` and the
  `TryParse` ASP.NET Core binds routes with. The data plugin stores it as its value, and numbers keys of `int` and
  `long` newtypes. [apps/tracker](apps/tracker/README.md) now keys its issues, projects and comments with newtypes,
  which also split its `NotFound(id: int)` error, used for both, into `NoIssue` and `NoProject`.

- **apps/tracker:** an issue tracker API written in Piper, to find what a real program needs. It has a domain with a
  workflow state machine, SQLite storage, a web API with policies, and tests at each layer.
  [apps/tracker/NOTES.md](apps/tracker/NOTES.md) records each gap it found, and what was done. From it:
  - **Enums:** every enum has `value.Name`, and a fieldless one has `E.Values` and `E.Parse(text) => E?`. A variant's
    JSON and printing show only its fields.
  - **Interpolated raw strings:** `$"""…{x}…"""`, and `$$"""…{{x}}…"""` where single braces are text, as in C#.
  - **`let _ = f();`** is a discard, so a block can have several.
  - **Nullable tasks:** a .NET call returning `Task<T?>` or `ValueTask<T?>` (`FirstOrDefaultAsync`, `ReadLineAsync`,
    `GetFromJsonAsync`) gives an Option when awaited, None for null, rather than a hidden null.
  - **State machines:** `Machine.Resume(state, …)` makes one in a saved state, and `CanFire(event)` checks an event
    given as data.
  - **Data:** fieldless enums are columns holding the variant's name (optional ones nullable), and `let` fields are
    columns.
  - **Web:** a `policies` block can take parameters, so limits can come from configuration.
  - **.NET statics:** a .NET type's static property or field can be assigned (`Environment.ExitCode = 2`).
  - **Name lookup:** a type position looks past a function or variable of the name, so a function `List` doesn't hide
    `List<T>`.
  - **Branches build a Result or Option together:** `match r { Ok(v) => Ok(v * 2), Err(e) => Err(e) }` and
    `if … { Some(x) } else { None }` infer their type from all the branches, where nothing else says it.
  - **Web:** a warning for an error type's field named `status`, which its ProblemDetails would leave out.
  - The tracker also has a webhook notifier with `[Retry]`, and an admin command line (`add-project`, `import`,
    `list`) from an `args` declaration. tests/AppsTests runs its tests with the rest.
- **apps/tracker-desktop:** an Avalonia window on the tracker's API, laid out in code with an MVVM view model using
  `[Notify]` and `[Command]`, and tested against a fake API. From it:
  - **Inherited .NET statics:** a static property or field declared by a base class can be named through a derived
    type, as in C# (`ComboBox.SelectedIndexProperty`, declared by `SelectingItemsControl`). Static methods already could.
  - **`_` lambda parameters** bind nothing, so `(_, _) => …` is allowed, as in C#.
  - **Aspects:** a `[Notify]` class's `OnCommandFailed(command: string, error: Exception)` method hears of each
    `[Command]`'s failure. Without one, an async command's exception is thrown on the synchronization context it
    started on, as `async void`'s is, rather than lost in a task nobody awaits.
- **apps/stock:** products as actors spread over several nodes on the remote actors plugin, behind an HTTP API on
  each, with a Dockerfile, a Docker Compose file and a Kubernetes manifest (a StatefulSet and a headless service) for
  running it as pods on AKS. A load run reserves one product through every node at once without overselling it, and a
  dashboard served by every node shows the nodes, their products and which node answered each request. From
  it:
  - **A file's class** (where its top-level functions go in C#) is no longer hidden by a member of the same name: a
    `Products()` method calling a function of products.piper was an internal compiler error.
  - **Setting a .NET `T?` value-type property** (`request.Headers.ConnectionClose = true`, or `None`) converts the
    Option to a Nullable, rather than being an internal compiler error.
- **apps/grid:** grid computing on the remote actors plugin. A coordinator shares a Monte Carlo Value at Risk among
  workers that ask it for chunks, so workers (Kubernetes pods in a Deployment) can be scaled mid-job. A chunk not sent
  back within its lease goes to another worker, and per-chunk seeds give the same figures however the work was
  shared. Its dashboard shows the chunks, the workers, the result and the history. It needed no compiler fixes.

- **Expression trees:** a lambda passed where .NET takes an `Expression<Func<…>>` is an expression tree, as in C#. So
  a query on an `IQueryable` uses `Queryable`'s operators, which a provider such as EF Core translates to SQL, rather
  than `Enumerable`'s, which ran it in memory. `let f: Expression<Func<int, bool>> = x => x > 1;` works too.
  - What a tree can't contain is error PIP0397: statements, assignment, `await`, `throw`, `match`, `is` patterns,
    tuples, and `??` or `?.` on optionals (compare with `None` instead).
- **Fix:** where a .NET method has a generic and a non-generic overload, `IEnumerable<T>` over a Piper type now counts
  as more specific than `IEnumerable`, as C# does, so `people.AsQueryable()` is an `IQueryable<Person>`.
- **Fix:** overriding a .NET `protected internal` method (`DbContext.OnModelCreating`) emits `protected`, as C#
  requires across assemblies, rather than C# that didn't compile.
- **Native libraries in packages** (SQLite's `e_sqlite3`) are loaded for `piper run` and `piper test`, and copied next
  to a program by `piper build`.
- **`config` plugin:** `config Smtp from "Smtp" { port: int = 587 requires port > 0; … }` becomes a record, and
  `LoadSmtp(configuration)` / `SmtpFrom(configuration)` reading it from an `IConfiguration`. Every missing, unparsable
  or invalid value is reported at once, with its key. Fields can be optional, lists, or nested configs.
- **`data` plugin:** `database Shop { table Orders: Order key id index customerId; }` becomes an EF Core `DbContext`.
  Options are nullable columns, compared with `None` as SQL's `IS NULL`, through Piper.Data, which comes with the
  plugin. A `key` or `index` naming a missing field is an error.
  samples/data uses it with SQLite, and with `config` for its settings.

- **Web: who may call an endpoint, and how often.** `authorize`, `authorize "admin"`, `anonymous`,
  `ratelimit "api"`, `ratelimit off`, `cors "partners"` and `cache "short"` after a group's or an endpoint's route
  (an endpoint's replacing its group's for the same concern), becoming ASP.NET Core's endpoint conventions.
  - A `policies` declaration defines them: authorization policies with `roles` and `claim`; `fixed`, `sliding`,
    `bucket` and `concurrent` rate limits (`fixed 100 per minute`), answered 429; CORS `origins`; and output cache
    `expire` times. It becomes `fn Name(services)`, registering them, and `fn UseName(app)`, adding the middleware
    in the order ASP.NET Core needs.
  - Checked: `authorize` with `anonymous`, a repeated word, and a missing policy name are errors. An endpoint naming
    an undeclared policy, a policy no endpoint uses, and policy words without any `policies` block are warnings.
  - Plugins can keep notes on an expansion for their analysis: `PluginParser.Data`, read back as `PluginExpansion.Data`.

- **Decorators:** a plugin can add attributes that rewrite the function or method they're on,
  `[Retry(3)] fn Fetch() { … }`, for cross-cutting concerns such as tracing, timing and retrying. It's code
  generation at compile time: the result is ordinary Piper, type-checked as usual, the attribute never reaches C#, and
  nothing happens at run time beyond the code written.
  - Plugin API: `Decorator`, given a `DecoratorContext` with the function, the attribute's arguments, a template,
    fresh names, and `WithPrologue` and `WithBody` to rewrite it.
  - Several on one function apply nearest first. `requires` clauses are still checked before a decorator's code.
  - A decorator on anything but a function or method with a body is error PIP0507.
  - A new bundled plugin, `aspects`, has `[Trace]`, `[Timed]` and `[Retry(times, delayMs)]`.
  - **On classes and records** too, as a C# source generator: a `Decorator`'s `Type` handler gets a
    `TypeDecoratorContext` and returns the type rewritten, plus any declarations to add beside it (a record's new
    methods, in an `extend` block). `ClassMembers`, `WithMembers`, `WithBase` and `DecorateMethods` help write it.
  - `[Notify]` (in `aspects`) makes a class implement `INotifyPropertyChanged`, for WPF and Avalonia data binding. Each
    settable property and public `mut` field raises `PropertyChanged` when it changes, and so do the computed
    properties that use it. `[Trace]` on a class traces each of its methods.
  - **Markers:** a `Decorator` with `ReadBy` set does nothing itself. It stays on a class's members for the named
    type decorator to read, and is error PIP0508 anywhere else.
  - `[Notify]` reads three: `[Command]` on a method adds an `ICommand` property (`Save` gets `SaveCommand`), whose
    `CanExecute` is a `CanSave` method or property and which raises `CanExecuteChanged` when what that reads changes;
    `[NotifyIgnore]` leaves a property alone; and `[AlsoNotify("Total")]` raises other properties too.
- **Fix:** a lambda or local function in a record struct's method can use `this` and its fields. The emitted C# didn't
  compile (CS1673); the method now copies `this`, which is readonly, for its closures to use.

- **Dictionary literals:** `["Ada": 36, "Alan": 41]` is a `Dictionary<string, int>`, and `[]` one where a dictionary
  is expected. Spreads add another dictionary's entries, with later keys replacing earlier ones:
  `[...defaults, "port": "8080"]`.
  - A constant key written twice is error PIP0395, and an element that isn't an entry or a spread PIP0396.
  - `Piper.Runtime` gains `PiperDictionary.Of`, which literals with spreads compile to.

- **Record structs:** `record struct Vec(x: double, y: double);` is C#'s `readonly record struct`: a record that's a
  value type, copied rather than shared and allocating nothing, with everything a record has.
  - A record struct that holds itself inline is error PIP0394.
  - `Vec[3]` works when its default (all zeros) is a valid value: every field's default is, and it has no invariants.

- **Bitwise operators:** `&`, `|`, `^`, `~`, `<<` and `>>`, with C#'s precedence, typing and results.
  - On integers and chars, on bools (`&`, `|` and `^`, which always evaluate both sides), on .NET enums as flags
    (`FileAttributes.ReadOnly | FileAttributes.Hidden`, `~FileAttributes.ReadOnly`), and through .NET types' own
    operators (`BigInteger`).
  - `>>` is two `>` tokens joined by the parser, so `List<List<int>>` still closes.
  - Constants fold, so `let mask: byte = 0x0F | 0x30;` fits a byte.
  - The precedence levels in SyntaxFacts are renumbered to fit them in; the order of the existing operators is unchanged.
- **More compound assignments:** `&=`, `|=`, `^=`, `<<=`, `>>=`, and `??=`, which sets an optional only when it has no
  value, evaluating the right side only then. Its target must be a name, a member of one or an index of one (PIP0389),
  and optional (PIP0388).
- **`not`, `and` and grouping in patterns:** `x is not null`, `c is >= 'a' and <= 'z'`, `not (Red | Green)`, as C#'s
  `not`, `and` and parentheses.
  - They count towards exhaustiveness as what they mean, so `match o { not null => …, null => … }` needs no `_`.
  - `not` can't bind variables (PIP0390); a `not` or `and` pattern nothing matches is an error (PIP0393), as in C#.
  - `not` and `and` are keywords only in patterns, so they can still be names.
- **Labelled loops:** `outer: for … { for … { break outer; } }`, and `continue outer;`, on `while`, `for` and
  `await for`, also in match arms. They become `goto`s to labels after the loop or at the end of its body.
  - New errors PIP0112 (a label on something other than a loop), PIP0391 (a label already used by a loop around it)
    and PIP0392 (no loop around the jump has that label).

- **Events in classes:** `event Changed: EventHandler;` is a C# field-like event, of a delegate type returning nothing
  (a .NET one or a function type).
  - The class raises it by calling it (`Changed(this, args)`), and anyone adds and removes handlers with `+=` and `-=`.
  - A class implements a .NET interface's events, so `class Person: INotifyPropertyChanged` can now be written, for
    WPF and Avalonia data binding.
  - New errors PIP0385 (an event's type) and PIP0386 (raising it outside its class).
- **Type aliases:** `type Lookup = Dictionary<string, List<int>>;`, interchangeable with the type, as C#'s
  `using Lookup = …;`. Aliases may name other aliases, in any order; a cycle is error PIP0381 and type parameters
  PIP0110.
- **`params` parameters:** `fn Log(format: string, params args: object[])` takes any number of arguments as an
  array, or an array itself, as in C#. Only on a function's or method's last parameter (PIP0111, PIP0382).
  - A collection literal passed to a generic `T[]` parameter now infers T from its elements (`First([1, 2])`).
- **`lock`:** `lock gate { … }` (or `lock (gate) { … }`), as C#'s, on a .NET 9 `Lock` or any object.
  - Locking a value is error PIP0383, and awaiting inside the block PIP0384.
- **Data-race warnings (PIP0387):** in a parallel loop's body (`parallel for`, `Parallel.ForEach`) or a group of
  tasks (`concurrency { }`, `Task.WhenAll(Task.Run(…), …)`), changing a variable, field or non-thread-safe collection
  that other threads use, outside a `lock`, is a warning.
- **Counting from the end and slicing:** `xs[^1]` is the last element, and `xs[1..3]` a slice, on arrays, strings and
  .NET collections (anything with a `Length` or `Count`, and for slices a `Slice(int, int)`, such as `List<T>`).
  - As in a `for` loop, a range includes its end unless it's `..<`, so `xs[1..3]` is three elements and `xs[1..<3]`
    two (C#'s `xs[1..3]`).
  - Either end can be left out (`xs[2..]`, `xs[..<2]`) or count from the end (`xs[1..^1]`, to the last element).
  - They're written as C#'s `^` and `..`.
  - New errors PIP0373–PIP0379, for a `^` outside `[…]`, something that can't be counted from the end or sliced, a
    slice that isn't alone in `[…]`, `^0` as an element, `..<` without an end, and a `for` loop over an open range.
- **`nameof` and `typeof`,** as in C#.
  - `nameof(order.Total)` is the constant `"Total"`, once the name is checked; it can name a variable, member,
    instance member through its type, enum variant, type or namespace. Renaming what it names renames it too.
  - `typeof(Point)` is a `System.Type`, and works in attribute arguments: `[JsonDerivedType(typeof(Circle), "circle")]`.
  - Both are keywords only before `(`. New error PIP0380, for a `nameof` of something that isn't a name.
- **State machines plugin:** `plugins/Piper.Plugins.StateMachines` adds `statemachine` declarations, with states,
  events with parameters, `on Pay(amount: decimal) => Paid if … { … }` transitions with guards and actions, and
  `enter`/`exit` blocks.
  - Each machine becomes a class with a method and a `Can…` check for each event, `State`, `IsFinal` and `Events`,
    plus an enum of its states and one of its events (`Fire` replays those).
  - Its shape is checked, and states nothing reaches are warned about.
  - It comes with piper as `state-machines`. See `samples/state-machines`.
- **Workspace trust:** in a folder the editor hasn't trusted, the language server loads only the plugins that come
  with piper or were added. It leaves out plugins given by path, packages (whose restore runs the folder's MSBuild
  files) and assemblies, with a PIP0404 warning on piper.json.
  - VS Code's extension supports untrusted workspaces in a limited way, ignores a workspace's `piper.server.*`
    settings, and loads the rest once the workspace is trusted.
- **Plugin keywords in the editor:** `PluginKeyword.Description`, `Snippet` and `Words` give hover text for a keyword
  and its construct's words, and completion offers a plugin's keywords where they can start, with a snippet of the
  construct. Every bundled plugin has them.
- **`piper plugin new <folder>`:** makes a plugin project (adding a `repeat` statement) and an example program.
  - Each build adds the plugin to piper.
  - In this repository it references the compiler's project; elsewhere, the installed compiler.
- **Clearer plugin version errors:** a plugin that fails because it was built against another compiler (a missing
  method or type) is told to rebuild. One built against a newer compiler is refused when it's loaded.
- **`piper plugin add`:** adds a plugin under a name a piper.json can use, as it does those that come with piper,
  without a new copy of piper. The plugin's folder is copied to `~/.piper/plugins` (or `$PIPER_PLUGINS`).
  - Bundled names win, and the error for an unknown name lists the plugins that have been added.
  - `piper plugin list` and `piper plugin remove` manage them.
  - The language server asks the editor to watch that folder, so a plugin added while the editor is open is picked up
    straight away, highlighting included.
- **Plugins' words are highlighted:** the editor colours the words special to a plugin's construct as keywords, as it
  does the plugin's keyword: `on` in an actor; `state`, `on`, `enter` and `exit` in a state machine; `singleton` in
  `services`; `get` in `endpoints`. A plugin reads them with the new `PluginParser.ReadWord()` or with `ExpectWord`.
- **Grid computing sample:** `samples/grid` uses the remote actors plugin to spread a job over worker nodes on many
  machines. Workers take the next chunk when they're free, and a failed or unreachable node's chunks go to the
  others.
- **Options and Results as web responses:** an `endpoints` handler returning `T?` answers 200 with the value or 404
  for None, and one returning `Result<T, E>` answers 200 or the error as ProblemDetails, so the
  `match … { Some(x) => Results.Ok(x), None => Results.NotFound() }` boilerplate goes away.
  - `[Status(409)]` (from `Piper.Web`) on an enum variant, or on a whole enum or record, chooses an error's status;
    without one it's a 400. The error's JSON (a variant's `kind` and fields) becomes ProblemDetails extension members,
    and a string error is its `detail`.
  - It's an endpoint filter added only to handlers returning an Option or Result, so others are unchanged.
- **Attributes on enum variants:** `enum LoanError { [Status(404)] UnknownBook(code: string), … }`, put on the
  variant's C# class.
- **Plugins can ship assemblies** for the code they write to call (`IPiperPlugin.References`, relative to the plugin's
  assembly). A program using the plugin references them, and a built one gets copies.

- **Trailing closures:** a block after a call is its last argument, as a lambda: `Repeat(3) { i => … }`,
  `Guarded { … }`. In an `if`, `while`, `for`, `match` or `requires` head, a `{` is still the body. A closure that
  awaits is async.
- **Fix:** a lambda capturing a range loop's variable (`for i in 0..<3 { actions.Add(() => i); }`) now captures each
  iteration's value, as it already did when looping over a collection, rather than the last one.
- **Better errors:** an error in a lambda passed to an overloaded .NET method (`Parallel.ForEach`, `Task.Run`,
  `List.ForEach`) is reported where it is, rather than as "no overload accepts these arguments".
- **Packages, frameworks and assemblies:**
  - `piper.json` can list `"packages"` (NuGet), `"frameworks"` (such as `Microsoft.AspNetCore.App`) and `"references"`
    (.NET assemblies).
  - The binder, `piper run`, `piper test` and the editor use them, and `piper build --exe` copies the package and
    referenced assemblies next to the program, naming the frameworks in its runtimeconfig.json.
  - Packages are restored with the .NET SDK and cached in the folder's `obj/piper`.
  - New errors PIP0401–PIP0403.
  - See `samples/web`, a minimal API with dependency injection.
- **Classes:** `class Account(owner: string) { mut balance = 0m; fn Deposit(amount: decimal) { balance += amount; } }`
  declares a reference type with identity and state that can change, as a C# class with a primary constructor.
  - Fields (`let`, `mut`) are private and methods public unless marked otherwise; inside methods, fields, constructor
    parameters and other methods are named bare, or through `this`.
  - A class derives from one Piper or .NET class (`class NotFound(id: int): Exception("No item {id}")`), then any
    traits and .NET interfaces. `abstract`, `sealed`, `virtual`, `override` and `abstract fn` work as in C#, and
    overriding public and protected .NET methods (`ToString`, `Collection<T>.InsertItem`) is checked against their
    signatures.
  - `base.F()` calls the base class's version of a method, Piper or .NET (protected ones too).
  - Type patterns test for a type and bind it: `match shape { c: Circle => …, _ => … }`, `if o is s: string { … }`,
    as C#'s `shape is Circle c`. They work on classes, records, traits, interfaces, .NET types and `object`.
  - Casts go down to a derived class: `(Circle)shape`.
  - **Static fields:** `static mut issued = 0;`, `public static let Default = Config("default");`. One per class (per
    instantiation for a generic class), named bare inside it and as `Ticket.issued` outside. Their initializers run in
    the order they're declared, and using one before it's initialized is an error rather than C#'s silent default.
  - **Invariants:** `class Account(owner: string, opening: decimal) requires opening >= 0m { … }`. The conditions are
    over the constructor's arguments, and are checked whenever one is made, before the field initializers and the base
    constructor run, and at compile time for constant arguments (PIP0340), as for records.
  - Field initializers can call the class's static methods bare.
  - **Properties:** `prop Balance => decimal = balance;`, `prop Count: int { get; private set; } = 0;`,
    `prop Name => string { get = name; set { name = value.Trim(); } }`. Read and set like fields; public unless marked
    `private`, with an optional `private set`. They can be static, `virtual`, `abstract` and `override`, override .NET
    properties, and implement .NET interfaces' properties (`IReadOnlyCollection<T>.Count`), so a class can now derive
    from a .NET base with abstract properties, such as `Stream`. New errors PIP0355 and PIP0356.
  - **Indexers:** `prop this[x: int, y: int] => int { get = …; set { … } }`, used as `grid[1, 2] = 5`. They can be
    overloaded by parameter types, be virtual, abstract or overridden, and implement .NET interfaces' indexers
    (`IReadOnlyList<T>`).
  - **Constructors** besides the primary one, written as in C#: `Account(owner: string): this(owner, 0m) { … }`. A
    creation, or a derived class's base list, chooses among them by argument types. They can be private or protected.
  - **`init` properties:** `prop Opened: DateOnly { get; init; } = …;`, settable only in constructors.
  - **`protected`** fields, properties, methods, indexers, constructors and setters (`protected set`), checked as C#
    does. Overrides keep the access of what they override, so an override of a protected .NET method can no longer be
    called from outside, which C# rejected.
  - **Attributes** on a class's fields, properties, indexers and constructors (`[JsonPropertyName("id")] public let Id`).
  - New errors PIP0357–PIP0359.
  - The editor's outline shows a class's fields, properties, indexers, constructors and methods; completion offers a
    class's public fields and properties; `prop`, `get`, `set`, `init` and `protected` are highlighted.
- **Properties in traits and `extend` blocks:** read-only, `trait Shape { prop Area => double; prop Label => string =
  "shape"; }`, implemented in `extend` blocks or by a class's properties, and read through a bound like
  methods. `extend int { prop IsEven => bool = this % 2 == 0; }` adds a C# 14 extension property.
- **Properties are written as functions or fields are:** one that computes its value is written as a function is,
  `prop Count => int = todos.Count;`, `prop Name => string { get = name; set { … } }`, `prop Area => double;` in a
  trait; one that stores it is written as a field is, `prop Count: int { get; private set; } = 0;`. The C# forms,
  `prop Count: int => …` and `get => …`, are errors saying how to write them.
- **Object initializers:** `Window("Piper") { Width: 640, Height: 480 }` sets properties and fields after the call that
  makes the object, as C#'s `new Window("Piper") { Width = 640, Height = 480 }` does, for Piper classes and .NET
  types. Each is set as an assignment would set it, except that `init` properties can be set there too; a `{` after a
  call that isn't followed by `Name:` is still a trailing closure. Setting a .NET type's `init` property elsewhere is now
  reported (PIP0359) rather than failing in the C# compiler. New errors PIP0360 and PIP0361.
- **`piper new`:** `piper new hello` makes a program folder with a `piper.json`, a `main.piper` (with a test) and a
  `.gitignore`; `--template web` makes a small web API instead, using the services and web plugins.
- **`piper run --watch`:** runs the program, and when a `.piper` file or `piper.json` changes (not under `bin/` or
  `obj/`), stops it and runs it again; a version that doesn't compile waits for the next save. A web server is
  restarted on its port, and a run ends with its watcher however that ends, so none is left holding the port.
- **Library visibility (breaking for libraries):** a top-level declaration is now visible throughout the program or
  library but not outside it, as C#'s `internal` (and C#'s default) is; `public` exports it as part of a library's API
  (`public record Point(…)`, `public fn Distance(…)`, `public extend string { … }`), and `private` still keeps it to
  its file. Programs are unaffected. A library built before needs `public` on what its users call. A public
  declaration can't mention a type that isn't public (PIP0363), and a declaration can't be both (PIP0362). Hovers
  show `public`.
- **Nested object and collection initializers:** in an object initializer, `Font: { Size: 14 }` sets properties of
  the object `Font` already holds, and `Tags: { "a", "b" }` adds to the collection `Tags` holds with its `Add`
  (`Scores: { { "x", 1 } }` for an `Add` taking several arguments), as C#'s `Font = { Size = 14 }` and
  `Tags = { "a", "b" }` do; they nest. The member must hold an object, not an optional (PIP0365) or a value type
  (PIP0366), and a collection must implement `IEnumerable` (PIP0367). Its `init` properties can't be set there, as in
  C#. A block straight after `Name:` in an initializer now needs parentheses. New errors PIP0364 to PIP0368.
- **Settable and static trait properties:** `prop Name => string { get; set; }` in a trait requires a settable
  property, which a class implements with a public settable property (or `public mut` field) and code sets through the
  trait or a bound (`pet.Name = "Rex"`, `item.Name += "!"`); only a class can implement one (PIP0371), and a compound
  assignment through an expression other than a name is reported (PIP0372). `static prop Zero => T;` is a static
  abstract property, with an optional default, implemented like a static method and read as `T.Zero` or `Money.Zero`.
  As C#: `string Name { get; set; }` and `static abstract T Zero { get; }`. **Fix:** a class can now implement a trait
  with static methods or properties; it was reported as using the trait as a value's type (PIP0319). A class property
  that doesn't match a trait's is now reported as not matching (PIP0246) rather than missing.
- **Fix:** a record with invariants is now JSON as any other record: its fields' attributes (`[JsonPropertyName]`,
  `[JsonIgnore]`), defaults and the serializer's options apply. Its converter used to read the fields itself and
  ignored them; the compiler now nests one in the record that has System.Text.Json read a copy of its fields, without
  reflection. An enum variant with a field JSON would name `kind`, where the variant's name goes, is now an error
  saying so when the enum is first read or written, rather than JSON that can't be read back.
- **Piper's types in JSON and web requests:** System.Text.Json now writes an Option as its value or `null` (it was
  `{"IsSome":…,"Value":…}`, and `null` couldn't be read back), reading a missing field as None; an enum's variant as its
  name or an object naming it in `"kind"` (it was `{}`); and JSON a record's `requires` rejects as a `JsonException`, so
  a web API answers it 400 rather than 500. An optional handler parameter in `endpoints` (`name: string?`) is None when
  the request leaves it out, rather than a 400, through `Option`'s new `TryParse`. The todo-api sample uses all of them.
- **.NET interface indexers that may take or give null:** a class can implement one (`string? this[string? key]`) with an
  indexer of Options (`prop this[key: string?] => string?`), as it already could a property; it was reported as an
  interface Piper can't implement.
- **`required` members:** making a .NET type with `required` members needs them set in its initializer
  (`Person() { Name: "Ada", Age: 36 }`), as in C#, unless its constructor is `[SetsRequiredMembers]`; one left out is
  reported (PIP0369) where it used to fail in the C# compiler, and so is one changed in braces rather than given a
  value (PIP0370). A Piper class deriving from such a type has them too, unless its base constructor sets them, and
  then its own constructors are marked as setting them. **Fix:** such types' constructors could not be called at all,
  as C# marks them obsolete for older compilers; that marker is now ignored, as C# ignores it.
- **The args plugin:** `args Options { port: int = 5080 "The port"; verbose: bool; files: List<string>; }` declares a
  program's command-line options as a record, with `Options.FromArgs(args)` (which prints the usage on `--help`, and
  the mistake and usage on a bad command line, and exits) and `Options.Parse(args)` (a Result, for tests). Flags,
  optional and required options, defaults, `--name=value`, `--` and the other arguments; typos suggest the nearest
  option. It comes with piper, and samples/todo-api and the web template use it.
- **Plugins come with piper:** the `piper` tool carries the actors, remote-actors, parallel, services and web plugins,
  which a `piper.json` names, `"plugins": ["services", "web"]`, rather than giving a path to a built copy. The samples
  name theirs, so they no longer need the plugins built first. A path still works for any other plugin.
- **Fix:** an Option of a value type passed where .NET takes `object?` (`Console.WriteLine(xs?.Count)`) generated C#
  that didn't compile; the value is now boxed, or null for None.
- **Bare calls in trait defaults:** a trait's default methods name its other methods and properties bare (`Name()` for
  `this.Name()`), as a class's methods do.
- **Fix:** a class deriving from a generic Piper class (`class Names: Repository<string>`) didn't see what it inherited:
  its fields, properties, indexers and methods weren't found, and its overrides were reported as overriding nothing.
  They're now found, with the base's type parameters replaced by the class's type arguments, as are the .NET members of
  a generic .NET class at the root (`class Ints: Bag<int>` where `class Bag<T>: List<T>`).
- **Services plugin:** `services { }` moves from the web plugin to its own, `plugins/Piper.Plugins.Services`, which
  brings in only the `Microsoft.Extensions.DependencyInjection` package, so dependency injection works in any program
  (see `samples/services`). The web plugin keeps `endpoints { }`; a web program lists both plugins.
- **Fix:** a .NET generic given a Piper trait or class as its type argument didn't take what implements or derives
  from it: `services.AddSingleton<Clock>(SystemClock())` and `List<Shape>().Add(Circle(1.0))` reported no matching
  overload. They now convert as Piper converts (boxing a builtin that implements the trait).
- **.NET variance:** `List<Circle>` converts to `IEnumerable<Shape>`, `Func<Dog>` to `Func<Animal>`, and an
  `IComparer<Shape>` to an `IComparer<Circle>`, as in C#, for assignments and arguments alike.
- **Functions as any delegate type:** a function named as a value converts to any delegate type with its signature, not
  just `Func` and `Action`: `numbers.ConvertAll(Square)`, `numbers.FindAll(IsEven)`, `words.Sort(ByLength)`,
  `Thread(Work)`, with a generic method's type arguments inferred from it (`Array.ConvertAll(array, Describe)`). A static
  method of a record, enum or class can be a function value too: `numbers.ConvertAll(Point.Make)`.
- **Fix:** lambdas' parameters were renamed apart in the generated C# when several in a function shared a name (`id`,
  `id_1`, `id_2`). ASP.NET Core binds route values by name, so every `endpoints` handler after the first taking `{id}`
  answered 400 Bad Request. Lambda parameters now keep their names unless that would hide something the lambda uses.
- **Sample:** `samples/todo-api`, a to-do REST server using `services` and `endpoints`; `--serve` keeps it running on
  localhost:5080.
- **`piper run app --port 8001`:** an option `piper run` doesn't know, after the program, goes to the program (with
  everything after it), as with `dotnet run`, so the `--` before the program's arguments can be left out. That also
  helps where a shell (Windows PowerShell) drops a bare `--`.
- **.NET enum casts:** a .NET enum casts to and from numbers, and to another enum, as in C#: `(int)response.StatusCode`,
  `(DayOfWeek)3`.
- **`None` for overloaded .NET methods:** `client.PutAsync(url, None)` used to fail, since `None` alone has no type and
  the method has `string` and `Uri` overloads. `None` now fits any parameter .NET lets be null, and takes its type from
  the overload chosen.
- **Fix (editor, Windows):** VS Code names files as `file:///d%3A/…`, which the language server read as
  `D:\d:\…`. It then couldn't find a program's `piper.json`, so a program's other files, its plugins (`services`
  reported as unexpected) and its references were missing, and problems were listed against a file that doesn't
  exist.
- **Fix (editor):** code a plugin generates has its construct's span (so errors point there), and the editor treated it
  as source: `services AppServices` was coloured as a namespace, and `endpoints` as a variable with a
  `: RouteGroupBuilder` hint. Generated code is now left out of hover, colouring, references and hints, and plugin
  keywords are coloured as keywords.
- **Go to Type Definition** in the editor: from a variable, field, parameter, expression or function to the declaration
  of its type, or of the Piper types in it (`List<Point>`, `Point?` and `(Point, Circle)` lead to Point and Circle).
- **Fix:** attributes on a class itself were silently dropped; they're now applied.
- **Fix:** a class method's parameter named like a primary constructor parameter was renamed in the C# (`owner_1`),
  breaking named arguments to it.
- **Fix:** a record invariant using an operator that needs lowering (`requires (max ?? 0) >= 0`) no longer crashes the
  compiler.
  - New errors PIP0109 and PIP0349–PIP0354.
- **Better error:** a collection literal whose elements have no common type says so, rather than calling the element a
  match arm.
- **Conditional expressions:** `c ? a : b`, shorthand for `if c { a } else { b }`, as in C#. A `?` is the conditional
  only when a `:` follows, so `x? + 1` keeps its meaning, and it works inside interpolation holes.
- **`?>`, the optional pipe:** `x ?> F(a)` calls `F(v, a)` when `x` is `Some(v)` (or `Ok(v)`), and passes `None` (or
  the `Err`) along otherwise, flattening steps that return an optional (or Result).
- **`ToString` on records and enums:** `extend Money { override fn ToString() => string = "£{this.amount}"; }` chooses
  how a record or enum prints.
- **.NET implicit conversions** apply as in C#: `BigInteger.Pow(2, 70)`, `let big: BigInteger = 5;`,
  `XElement("root", …)`.
- **`IEnumerable<T>`:** a record, enum or class implements it with one iterator method,
  `fn GetEnumerator() => IEnumerator<T> { … yield return x; … }`, and then works with `for`, LINQ and .NET APIs that
  take sequences. Piper supplies the non-generic `GetEnumerator`.
- **Comparison operators:** a type implementing `IComparable<T>` for itself gets `<`, `<=`, `>` and `>=`, as C#
  operators calling its `CompareTo`. Comparing types without it suggests implementing it.
- **Local functions:** `fn` inside a block declares a function usable throughout it (recursively, and before its
  declaration), which captures the variables around it, as C#'s local functions do. It can be generic, `async` or an
  iterator.
- **Remote actors plugin:** `plugins/Piper.Plugins.Actors.Remote` reads the same `actor` syntax on Proto.Actor, so actors
  can be used from other processes and machines: `Account.Spawn(system, "ada", …)` hosts one,
  `Account.Connect(system, "host:port", "ada")` reaches it, messages travel as JSON over gRPC, and faults and timeouts
  reach the caller. See `samples/remote-actors`.
- **`Main(args: string[])`:** `Main` can take the command-line arguments, as in C#; `piper run app -- one two` passes
  them.
- **Interop fixes:**
  - An interface's inherited properties can be read (`IList<T>.Count`).
  - An extension method is used when no instance method of the same name applies, as in C# (Proto.Actor's
    `RequestAsync` with a timeout).
  - A block ending in `throw` can be an argument or an initializer (`F({ throw E() })`), as `F(x ?? throw e)` can in C#.
- **Running programs:**
  - `piper run` names the program as `piper build` would, so libraries that send its types between processes agree with
    a built copy.
  - Package restores work for program folders given as relative paths.
- **Type tests in patterns:** a record or variant pattern can match a value typed `object`, a trait or an interface
  that the type implements, testing its type as C#'s `o is Point(var x, var y)` does: `match message { Add(n) => …, _ =>
  … }`.
- **Actors plugin:** `plugins/Piper.Plugins.Actors` adds `actor Name(params) { mut state = …; on Tell(x: int) { … }
  on Ask() => int { … } }`. Each actor has private state and handles one message at a time, on .NET channels: tells
  return at once, asks await the reply, and `Name.Spawn(…)` / `await using` start and stop it. See `samples/actors`.
- **Plugin analysis:** a plugin can implement `Analyze`, which sees the type-checked program and the code its keywords
  became, and reports errors or warnings (PIP0505, PIP0506). The actors plugin uses it to warn about messages
  carrying something mutable. Templates can splice parameter lists, and `PluginParser` adds `ParseAsyncBlock` and
  `ParseVariableDeclaration`.
- **Web plugin:** `plugins/Piper.Plugins.Web` adds two declarations, `services Name { singleton Clock = …; scoped
  Greeter => FriendlyGreeter; }` for dependency injection and `endpoints Name "/api" { get "/hello/{name}" (name:
  string) => …; }` for minimal APIs. It checks for duplicate services and routes, route parameters without a handler
  parameter, and untyped handler parameters, and brings in ASP.NET Core itself. See `samples/webapp`.
- **Lambdas as `Delegate`:** a lambda whose parameters have types can be passed where .NET takes any `Delegate`, as
  ASP.NET Core's `MapGet` does.
- **Compiler plugins:** .NET assemblies listed in `piper.json`'s `"plugins"` add statement and declaration keywords,
  which they rewrite into ordinary Piper as it's parsed. Everything after parsing works as for code the user wrote,
  including type checking, error locations and the language server. The new diagnostics are PIP0500–PIP0504. See
  [docs/plugins.md](docs/plugins.md), the sample plugin in `plugins/Piper.Plugins.Parallel` (`parallel for`,
  `concurrency { }`, which is awaited in async code) and `samples/plugins`. `SyntaxWalker` moved from the language
  server to `Piper.Syntax`, so plugins can use it.

## 1.0.0

The first release. From here on, a program that compiles with one 1.x release compiles with every later 1.x release
and means the same thing (see [Compatibility](README.md#compatibility)).

### The language

- **Records and enums:** immutable records with value equality and `{ ...p, y: 5 }` copies; enums as sum types whose
  variants carry fields.
- **Pattern matching:** exhaustive `match` with guards, nested patterns, literals, ranges (`1..9`, `0..<n`),
  comparisons (`< 0`), list patterns (`[first, ...rest]`), property patterns (`Point { x: 0 }`, `{ Length: > 5 }`),
  `|` alternatives that bind, and `is` tests. Missing cases are errors that name one; unreachable arms are warnings.
- **No null references:** `T?` is `Option<T>`, with `?.`, `??`, `?` and `null` as its None. .NET's nullable APIs are
  read as optionals.
- **Errors as values:** `Result<T, E>`, with `?` to propagate errors.
- **Everything is an expression:** blocks, `if` and `match` have values.
- **Traits, generics and extensions:** traits with default, static and generic methods and inheritance; generic records,
  enums, functions and methods with trait bounds; `extend` to add methods and traits to any type, including `int`,
  `string` and .NET types. Records and enums can declare arithmetic operators.
- **C#'s types and rules:** C#'s numeric types, conversions, overflow, casts, printing and equality.
- **.NET interop:** call any .NET API, implement .NET interfaces, use `out` and `ref` parameters, events, properties,
  indexers, operators (`date > other`, `end - start`), LINQ and .NET attributes (`[JsonPropertyName("id")]`, `[Obsolete]`).
- **Collections and lambdas:** collection literals with spreads (`[0, ...xs]`), arrays, tuples and deconstruction, lambdas
  and function types, the pipe operator `|>`.
- **Control flow:** `for x in xs`, `for i in 1..10`, `while`, `try`/`catch`/`finally`, `throw`, `using`, `defer`.
- **Async and iterators:** `async`/`await`, `await for`, `await using`, and `yield` iterators.
- **Programs:** namespaces, `using`, `private`, and programs of many files.
- **Preconditions and invariants:** `requires` clauses on functions and records, checked on every call and construction,
  and at compile time when the arguments are constants.
- **Bindings and calls:** `let … else` to unwrap or leave; named arguments and default parameter values, for Piper
  and .NET calls.
- **Strings:** C#'s alignment and format specifiers in interpolation (`{price:C}`, `{name,-10}`).
- **Doc comments:** `///` documentation, shown in the editor and written as C# XML documentation.
- **Tests:** `test "name" { … }` blocks with `Assert` and `AssertEqual`.
- **Conventions:** functions and methods are PascalCase (`Print`, `Main`), variables and fields camelCase, as in C#.
  `let` binds immutably and `mut` mutably. Some words are reserved for future versions, such as `const`, `type` and
  `class`.

### Tools

- **The `piper` tool** (`dotnet tool install -g Piper`):
  - `piper run` transpiles, compiles and runs a program;
  - `piper build` writes the C#, or with `--exe` or `--library` compiles an assembly with a PDB;
  - `piper test` runs a program's tests;
  - `piper emit` prints the C#;
  - `piper lsp` runs the language server.
- **`piper.json`** in a program's folder can set its assembly `name` and `output` (`exe` or `library`).
- **The Piper.Build package** compiles a .NET project's `.piper` files with its C#.
- **Source mapping:** errors, stack traces and the debugger point at `.piper` lines.
- **The language server** gives any LSP editor:
  - diagnostics as you type;
  - hover and go to definition;
  - find references and rename;
  - completion and signature help;
  - inlay hints for inferred types;
  - semantic highlighting;
  - a quick fix that adds a match's missing arms;
  - an outline.
- **The VS Code extension:**
  - syntax highlighting and the language server's features;
  - running a program, and its tests, from the editor;
  - debugging with breakpoints in `.piper` files, through the C# extension's debugger.
