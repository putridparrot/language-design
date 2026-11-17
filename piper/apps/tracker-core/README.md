# Tracker Core: what the tracker's server and clients share

The issues and their workflow, the records the tracker's API exchanges, and the API itself:

- **[issues.piper](issues.piper):** priorities, the ids (newtypes), an issue, its `Workflow` (a `statemachine`), and
  `IssueError`, each variant with the HTTP status the API answers it with.
- **[api.piper](api.piper):** the records the API sends and takes (`Project`, `IssueView`, `Comment`, `NewProject`,
  `NewComment`), and `api TrackerApi`, the [api plugin](../../docs/plugins.md#api-an-apis-shape-and-a-typed-client)'s:
  each call's route, what it takes and what it answers.

It's a library that [apps/tracker](../tracker/README.md) (the server) and
[apps/tracker-desktop](../tracker-desktop/README.md) (a client) each name in their piper.json's `"projects"`, which
compiles its source into them. The server's `endpoints Api: TrackerApi` must answer every call, as the call says; the
desktop calls them through the client `api TrackerApi` generates. So a change to the API that either side doesn't
follow fails to compile, rather than failing when they talk.

**[TrackerApi.shape.json](TrackerApi.shape.json)** records the API's shape: its calls and the types they send and
answer. Each build checks the API against it, so a change that would break a desktop already installed is an error
(a call removed, a new field clients must send, a new `WorkflowState` they can't read). Compatible changes update it;
`--accept-breaking` records a deliberate break. Check it in with the code.

```
piper test apps/tracker-core       # its own tests: the workflow
```
