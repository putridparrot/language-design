# Tracker Desktop: a window on the Tracker API

A desktop app for [the Tracker API](../tracker/README.md), written in Piper on Avalonia. It lists a project's issues
by status and title, moves them through their workflow, adds issues with a description, and shows and adds
comments. Making projects, an admin's job, stays with the tracker's command line.

What writing it found in Piper is in [the tracker's notes, phase 5](../tracker/NOTES.md#phase-5-a-desktop-client-avalonia).

## Starting and stopping it

It needs the API running, and a project to look at. From the `piper` folder, with a `piper` tool built from this
repository (see [the tracker's README](../tracker/README.md#getting-the-piper-tool)):

```
piper run apps/tracker                                       # in one terminal: the API, on http://localhost:5080
piper run apps/tracker -- add-project WEB Website            # once, if there are no projects yet

piper run apps/tracker-desktop                               # in another: signs in as your user name
piper run apps/tracker-desktop -- --user ada                 # as someone else
piper run apps/tracker-desktop -- --api http://localhost:6000   # an API on another port
piper test apps/tracker-desktop                              # its tests, against a fake API: no API or window needed
```

**Stopping it:** close the window. Stop the API with Ctrl+C in its terminal. The app keeps nothing of its own; the
issues and comments are in the API's database (see [the tracker's README](../tracker/README.md#starting-and-stopping-it)
to clear it).

If the API isn't running, the window still opens, and its status line says why it can't load the projects. Start the
API and open the app again (Refresh reloads a project's issues, not the list of projects).

## How it works

- **[app.piper](app.piper):** the options (`--api`, `--user`), and Avalonia started with its Fluent theme, showing the
  main window.
- **[client/client.piper](client/client.piper):** `TrackerClient`, the API's calls over `HttpClient`, signing in with
  the `X-User` header. Each call returns a `Result`, so a failure (the API down, a 409 for an action the issue's
  status doesn't allow) is a value the view model shows, not an exception.
- **[viewmodels/main.piper](viewmodels/main.piper):** `MainViewModel`, with the aspects plugin's `[Notify]` (its
  properties raise `PropertyChanged`) and `[Command]` (its async methods become commands for buttons). Buttons enable
  themselves from computed properties: `CanStart` is true when the chosen issue's `next` actions include Start. Choosing a
  project or status reloads the issues, and choosing an issue loads its comments. A command that fails is reported
  to `OnCommandFailed`, which shows it in the status line.
- **[views/main-window.piper](views/main-window.piper):** the window, laid out in code, as Piper has no XAML. Object
  initializers make the control tree read much like XAML would, with bindings to the view model's properties and
  commands.

The tests drive the view model against a fake API (an `HttpMessageHandler` answering as the API would, and one
that fails), so they need neither a window nor a server.

## Limits

- **A console window opens beside it** when it's built with `piper build --exe`, as Piper has no `WinExe` setting yet.
- **Signing in is the API's development sign-in:** whoever you say you are.
