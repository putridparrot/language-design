# To-do API

A small REST server in Piper: services registered for dependency injection with `services { }`, routes mapped
with `endpoints { }`, an OpenAPI document describing them, and tests that call it from memory. It uses the
[services](../../plugins/Piper.Plugins.Services), [web](../../plugins/Piper.Plugins.Web) and
[openapi](../../docs/plugins.md#openapi-openapi-documents) plugins.

## Running it

The plugins come with piper, so `piper.json` names them, `"plugins": ["args", "services", "web", "openapi"]`. Serve on
<http://localhost:5080> until Ctrl+C:

```
piper run samples/todo-api --serve
```

(`piper run samples/todo-api -- --serve` does the same.) `piper run samples/todo-api --watch --serve` restarts the
server whenever you save a change to `main.piper`. From a clone without the `piper` tool installed, run the
CLI through dotnet, from the `piper` folder:

```
dotnet run --project src/Piper.Cli -- run samples/todo-api --serve
```

Without `--serve` it starts on a free port, calls each endpoint once, prints what comes back and stops, which is how
the test suite runs it.

Options, after `--serve` (`piper run samples/todo-api --help` lists them, and the
[args](../../docs/plugins.md#args-command-line-options) plugin reads them):

- `--port 5090` serves on another port than 5080 (if 5080 is taken, say by another copy still running).
- `--https` serves HTTPS too, on the next port (<https://localhost:5081>). It uses ASP.NET Core's development
  certificate; create and trust it once with `dotnet dev-certs https --trust` (which also renews one that's expired).

```
piper run samples/todo-api --serve --https --port 5090
```

## Endpoints

| Request | Does | Answers |
|---|---|---|
| `GET /api/todos`, `GET /api/todos?done=true` | lists the to-dos, or those done (or not) | 200 and a JSON array |
| `GET /api/todos/{id}` | finds one | 200 and the to-do, or 404 |
| `POST /api/todos` with `{"title": "…", "priority": "High", "note": "…"}` (priority and note optional) | adds one | 201 and the to-do, or 400 for a blank title |
| `PUT /api/todos/{id}/done` | marks one done | 200 and the to-do, or 404 |
| `DELETE /api/todos/{id}` | removes one | 200 and the to-do removed, or 404 |
| `GET /openapi/v1.json` | describes the API | 200 and its OpenAPI document |
| `GET /api/stats` | counts them | 200 and `{"total", "done", "open", "since"}` |
| `GET /api/hello?name=Ada`, `GET /api/hello` | greets | 200 and text |

## Trying it

With curl:

```
curl -X POST http://localhost:5080/api/todos -H "Content-Type: application/json" -d "{\"title\":\"Try Piper\",\"priority\":\"High\"}"
curl "http://localhost:5080/api/todos?done=false"
curl http://localhost:5080/api/todos
curl -X PUT http://localhost:5080/api/todos/1/done
curl -i http://localhost:5080/api/todos/5
curl http://localhost:5080/api/stats
curl "http://localhost:5080/api/hello?name=Ada"
curl -X DELETE http://localhost:5080/api/todos/1
```

Or in PowerShell:

```powershell
Invoke-RestMethod http://localhost:5080/api/todos -Method Post -ContentType application/json -Body '{"title":"Try Piper"}'
Invoke-RestMethod http://localhost:5080/api/todos
Invoke-RestMethod http://localhost:5080/api/todos/1/done -Method Put
Invoke-RestMethod http://localhost:5080/api/stats
```

The GET requests work in a browser too.

## What's where

- **The data** is records (`Todo`, `NewTodo`, `Stats`) and an enum (`Priority`), which ASP.NET Core turns into JSON and
  back: an optional field (`note: string?`) is `null` when it's None, and a variant is its name (`"High"`). `NewTodo`'s
  `requires` rejects a blank title while the request is read, which answers 400 before the handler runs.
- **Ids** are a newtype, `newtype TodoId = int;`: a to-do's id can't be mixed up with any other int, yet it's `1` in
  JSON, is read from the route (`/api/todos/1`) as its value, and sorts as an int does.
- **The services** (`services AppServices { … }`):
  - `Clock` is a trait, implemented by `SystemClock`.
  - `TodoStore` is a class whose constructor takes the `Clock`, which DI passes it. It's a singleton, so every request
    sees the same to-dos.
  - `Greeter` is transient, made afresh for each request.
- **The routes** (`endpoints TodoApi "/api" { … }`): each handler's parameters come from the route (`{id}`), the
  query string (`name`), the JSON body (`request: NewTodo`) or the services (`store: TodoStore`), and it answers with a
  value (sent as JSON), an Option (`store.Find(id)`: 200 with the to-do, or 404 for None) or a `TypedResults.…`
  status. Each is tagged `Todos`, the group's tag, except `/api/hello`, which gives its own (`tags "Greetings"`).
- **The server** (`server TodoServer(urls, quiet) { … }`) says what the app is made of: where it listens, its services,
  `problems` (errors as ProblemDetails), `openapi`, its endpoints, and health checks at `/healthz` (while it runs) and
  `/ready` (when the store answers too). It becomes `fn TodoServer(urls, quiet) => WebApplication`, which `Main` runs
  and the tests serve from memory. See [A server](../../docs/plugins.md#a-server).
- **The OpenAPI document** (`/openapi/v1.json`, from the server's `openapi;`) is made from the handlers' types, so it can't disagree with them: Find, Complete and Remove are 200 or 404, as they return
  Options; Add is 201, as it returns `TypedResults.Created`. Its schemas describe Piper's types as they're written
  in JSON: a `TodoId` as an integer, `note` as a string or null (and not required), a `Priority` as one of its names.
  Without `--serve`, the sample prints each endpoint's tags and statuses from it.
- **The tests** ([main.tests.piper](main.tests.piper), `piper test samples/todo-api`) serve the app `TodoServer` makes
  from memory with `TestClient` (from Piper.Web): no port, and an `HttpClient` calling it. They check a to-do's
  round trip, that a blank title is refused, what the OpenAPI document says, and the health endpoints.
