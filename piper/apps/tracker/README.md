# Tracker: an issue tracker API

An issue tracker written in Piper, to use the language as a real program would: a web API over a SQLite database,
with a workflow for issues, sign-in, rate limiting, webhooks and a command line. [NOTES.md](NOTES.md) records each
place where Piper got in the way while writing it, and what was done about it.

[apps/tracker-desktop](../tracker-desktop/README.md) is a desktop window on this API.

## Getting the `piper` tool

Every app in `apps/` is run with a `piper` tool built from this repository. From the `piper` folder:

```
dotnet pack src/Piper.Cli -c Release -o artifacts       # once, and after changing the compiler
dotnet tool uninstall -g Piper
dotnet tool install -g Piper --add-source artifacts
```

Or, without installing it, put `dotnet run --project src/Piper.Cli --` wherever `piper` appears below.

## Starting and stopping it

From the `piper` folder:

```
piper run apps/tracker                              # serves the API on http://localhost:5080
piper run apps/tracker -- --port 6000               # on another port
piper test apps/tracker                             # its tests
```

**Stopping it:** Ctrl+C in its terminal.

**Its data** is in `tracker.db` (with `tracker.db-shm` and `tracker.db-wal` beside it while it runs), in the folder
it was started from, so `piper/tracker.db` when started as above. The database and its tables are made the first
time it starts. To start again with nothing, stop it and delete those three files. The tests use databases of their
own, so they never touch it.

**Admin commands** work on the database directly, with the API running or not:

```
piper run apps/tracker -- --help                    # all of them
piper run apps/tracker -- add-project WEB Website
piper run apps/tracker -- import WEB issues.csv     # lines of: title,priority
piper run apps/tracker -- list WEB
```

## Trying the API

**Signing in:** send an `X-User` header naming the user. Add `X-Roles: admin` for admin actions, such as making
projects. This is sign-in for development; a real deployment would use cookies or bearer tokens.

```
curl http://localhost:5080/api/health
curl -X POST -H "X-User: root" -H "X-Roles: admin" -H "Content-Type: application/json" -d "{\"code\":\"WEB\",\"name\":\"Website\"}" http://localhost:5080/api/projects
curl -X POST -H "X-User: ada" -H "Content-Type: application/json" -d "{\"title\":\"Crash on save\",\"description\":\"\",\"priority\":\"High\"}" http://localhost:5080/api/projects/1/issues
curl -X POST -H "X-User: ada" -H "Content-Type: application/json" -d "{\"kind\":\"Start\",\"assignee\":\"ada\"}" http://localhost:5080/api/issues/1/events
curl -H "X-User: ada" "http://localhost:5080/api/projects/1/issues?status=InProgress"
```

| Method | Path | What it does |
|---|---|---|
| GET | `/api/health` | Says "ok"; anyone may call it |
| GET, POST | `/api/projects` | Lists projects, or makes one (admins only) |
| GET | `/api/projects/{id}/issues?status=…&q=…` | A project's issues, newest first, by status and words in the title; rate-limited |
| POST | `/api/projects/{id}/issues` | Adds an issue: `{"title", "description", "priority"}` |
| GET | `/api/issues/{id}` | An issue, with the actions it can take next (`next`) |
| POST | `/api/issues/{id}/events` | Moves it through its workflow: `{"kind": "Start", "assignee": "ada"}`, `{"kind": "Submit"}`, `{"kind": "Approve"}`, `{"kind": "Reject", "reason": …}`, `{"kind": "Close", "resolution": …}`, `{"kind": "Reopen"}`, `{"kind": "Unassign"}` |
| GET, POST | `/api/issues/{id}/comments` | Its comments, or adds one: `{"text"}` |

An action the issue's status doesn't allow is a 409, a missing issue or project a 404, and a request breaking a rule
(a blank title, a lowercase project code) a 400.

**Settings,** from `appsettings.json` in the folder it runs from, or the environment:

| Setting | Environment | Default |
|---|---|---|
| `Tracker:Connection` | `Tracker__Connection` | `Data Source=tracker.db`, a file in the folder it runs from. `--connection` overrides it. |
| `Tracker:SearchesPerMinute` | `Tracker__SearchesPerMinute` | 60 |
| `Tracker:Webhook` | `Tracker__Webhook` | none. When set, each changed issue is posted there as JSON, with up to three attempts. |

## How it works

Each folder has its tests beside its code, in `*.tests.piper` files. The issues, their workflow, the records the API
exchanges and the API's shape are in [apps/tracker-core](../tracker-core), which piper.json's `"projects"` compiles
into this program and into [the desktop client](../tracker-desktop/README.md).

- **The API's shape** ([tracker-core/api.piper](../tracker-core/api.piper)) is `api TrackerApi`, the api plugin's: each
  call's route, what it takes and what it answers. [api/api.piper](api/api.piper)'s `endpoints Api: TrackerApi`
  answers it, which is checked when it compiles, and its clients call it through the typed client it generates (a
  test in [api/api.tests.piper](api/api.tests.piper) drives the server with it).
- **The workflow** ([tracker-core/issues.piper](../tracker-core/issues.piper)) is a `statemachine` (the state-machines plugin):

  | Status | Actions it allows |
  |---|---|
  | Open | Start (with an assignee) → InProgress; Close (with a resolution) → Closed |
  | InProgress | Submit → InReview; Unassign → Open; Close → Closed |
  | InReview | Approve → Closed; Reject (with a reason) → InProgress |
  | Closed | Reopen → Open |

  `Apply` moves an issue as an event asks, or returns why it can't, as an `IssueError`: a `Result`, not an exception.
  Each error carries the HTTP status the API answers it with (`[Status(409)] NotAllowed(…)`).
- **Ids** are [newtypes](../../docs/language.md#newtypes): `IssueId`, `ProjectId` and `CommentId`, each an int to the
  database and in JSON, but not to each other, so a project's id can't be looked up as an issue's. Routes bind them
  (`/issues/{id:int}` gives an `IssueId`), and the database numbers them. A missing issue and a missing project are
  different errors, `NoIssue(id)` and `NoProject(id)`.
- **Storage** ([store/store.piper](store/store.piper)): projects, issues and comments in SQLite, through EF Core (the
  data plugin), with LINQ queries for searching.
- **The API** ([api/](api)): minimal API endpoints (the web plugin), with header sign-in, an admin policy, rate
  limiting on searches, and settings bound from configuration (the config plugin). After each change, the issue is
  sent to the webhook if there is one ([api/notify.piper](api/notify.piper)), tried up to three times with the
  aspects plugin's `[Retry]`.
- **The command line** ([cli/cli.piper](cli/cli.piper)): `serve` (the default) and the admin commands, from an `args`
  declaration (the args plugin).

The tests start the whole API on a free port with a database of their own, and call it over HTTP.
