# Grid: sharing out a long calculation

A grid computing app written in Piper on the remote actors plugin. A coordinator splits a long calculation into chunks,
and workers (processes, containers, or Kubernetes pods) ask it for one when they're free. Workers can be added or
removed while a job runs, and a chunk whose worker vanished is done by another, without changing the answer.

The calculation is a Monte Carlo Value at Risk for a stock portfolio: simulate millions of possible ten-day futures,
and find the loss that only 1 in 100 of them is worse than.

```
                      coordinator ("jobs" actor + dashboard)
          "any work?" ▲  │ chunk 37        ▲  │ chunk 38        ▲  │ "nothing yet"
                      │  ▼                 │  ▼                 │  ▼
                   worker pod          worker pod          worker pod   ← kubectl scale: more just start asking
                      └── sample of chunk 37 ──▶ coordinator: kept (a late copy would be ignored)
```

## How it differs from apps/stock

[apps/stock](../stock/README.md) is about **owning state**. Each product's count lives in one actor on one pod, so
which pod is chosen by a hash of the product, from a fixed list of pods. This app is about **spreading work**. The
pieces have no state of their own, so any worker can do any one, and what matters is keeping every worker busy:

- **Workers ask for work** ([jobs/jobs.piper](jobs/jobs.piper)). A worker that finishes a chunk asks for the next, so
  faster workers do more. A worker only needs the coordinator's address, and the coordinator learns of workers by
  their asking: there's no list of nodes, so scaling the workers needs no restart.
- **A chunk is lent, not given** ([jobs/board.piper](jobs/board.piper)). If it hasn't come back within its lease
  (four times as long as chunks have been taking, and at least five seconds), the next worker to ask gets it again.
  Whichever copy comes back first is kept, and any later one ignored.
- **Same job, same answer** ([risk/risk.piper](risk/risk.piper)). A chunk's random numbers come from its own seed,
  and the samples are added up in chunk order, so a job gives exactly the same figures however the chunks were shared
  out, and however many were done twice. The dashboard's history shows this: rerun a job and it says "same as job N".
- **The rules are functions; the actor applies them.** An actor can only hold state and handle messages, so the
  coordinator's rules (start, hand out, take back, cancel) are functions from one board to the next. The `Jobs` actor
  holds the board and applies them one message at a time, which is what makes workers asking at once get different
  chunks. Being functions, they're tested with a pretend clock and no actors at all.
- **A coordinator that restarts carries on** ([jobs/jobs.piper](jobs/jobs.piper)). `Jobs` is a persisted actor: its
  events are the rules' inputs with the time they happened (`Started`, `Taken`, `Completed`, `Cancelled`), so
  replaying them rebuilds the board exactly: chunks done stay done, chunks out are still lent to their workers (who
  send them back to the new coordinator, as workers retry), and the history is kept. A worker told to wait is only
  noted in `transient` state, as idle workers ask every half second, which isn't worth keeping.

## The dashboard

The coordinator serves it at `/`:
- **Tiles:** the job's progress, how many workers are asking, and how many chunks were handed out again.
- **A cell for each chunk:** waiting, being worked on, done, or done after being handed out again. Hover over one to
  see which worker did it, and how long it took.
- **The workers,** with how many chunks each has done in this job. A worker not heard from lately is crossed out.
- **The result:** a histogram of the simulated profit and loss, with the 95% and 99% VaR marked, the expected
  shortfall, and the 99% VaR from a formula, as a check.
- **The history,** with each run compared with the last one of the same job.

## Running it

From the `piper` folder, with a `piper` tool built from this repository (see
[the tracker's README](../tracker/README.md#getting-the-piper-tool)).

**In one process:** `piper run apps/grid` starts a coordinator and three workers, each on its own node, so the work
still goes over the network. Open http://127.0.0.1:8090 and press Start.

**As separate processes:** a coordinator, then as many workers as you like, in other terminals or on other machines
(give `--host` an address the others can reach):

```
piper run apps/grid -- coordinator
piper run apps/grid -- worker --name one
piper run apps/grid -- worker --name two --workers 4       # four chunks at once
```

Stop a worker with Ctrl+C mid-job and its chunk is done by another; start another and it joins in.

**Stopping it:** Ctrl+C in each terminal, the coordinator's included. Where the jobs' events are kept is
configuration: in memory unless it says otherwise, so a coordinator that's stopped starts afresh. To keep them in a
SQLite file, so a coordinator stopped mid-job carries on with it:

```
Events__Provider=sqlite Events__Connection="Data Source=grid.db" piper run apps/grid -- coordinator
```

(or `sqlserver`, `postgres`, for a database in a cloud; see the Providers section of
[docs/plugins.md](../../docs/plugins.md)).

**`piper test apps/grid`** tests the risk figures, the rules for handing out chunks, and a job over HTTP/2 on three
worker nodes (a cluster in one process), where a fourth worker takes a chunk and is cut off by the network (its chunk is done by another, and
its answer, once it's back, ignored). Some are property tests: a job's figures are the same
for any number of chunks coming back in any order, and a sample counts every scenario, for any count and seed.

## On Docker Desktop's Kubernetes

In a namespace of its own, with the image built locally. The dashboard's LoadBalancer is published on localhost:8090.

```
docker build -f apps/grid/Dockerfile -t grid:local .
kubectl --context docker-desktop create namespace grid
sed 's#REGISTRY/grid:1#grid:local#' apps/grid/k8s/grid.yaml | kubectl --context docker-desktop -n grid apply -f -
kubectl --context docker-desktop -n grid rollout status deployment/grid-worker
```

Open http://localhost:8090, start a job, and while it runs:

```
kubectl --context docker-desktop -n grid scale deployment grid-worker --replicas=6    # more workers join in
kubectl --context docker-desktop -n grid delete pod <a grid-worker pod>               # its chunk is redone
kubectl --context docker-desktop delete namespace grid                                # clearing up
```

In PowerShell, the `sed` line is
`(Get-Content apps/grid/k8s/grid.yaml) -replace 'REGISTRY/grid:1', 'grid:local' | kubectl --context docker-desktop -n grid apply -f -`.

**Stopping it:** deleting the namespace removes everything in it, and frees port 8090. `docker image rm grid:local`
removes the image too.

[k8s/grid.yaml](k8s/grid.yaml), written by each build from [deploy.piper](deploy.piper) (the deploy plugin), makes:
- **`grid-coordinator`,** one pod, and a service of the same name that the workers ask for work. The coordinator
  tells the workers that name to reply to.
- **`grid-worker`,** a Deployment of three pods, each with one CPU and working on one chunk at a time. Each tells the
  coordinator its pod's IP address to reply to, so unlike the stock app's pods they need no stable names.
- **`grid`,** a LoadBalancer for the dashboard.

On AKS, it's as for [the stock app](../stock/README.md#on-aks), with `grid` for `stock`. Then
`kubectl scale deployment grid-worker --replicas=20` spreads the workers over the cluster's nodes, and adding nodes
to the cluster adds room for more.

## What it doesn't do

- **Its events are kept for ever.** Every chunk handed out and sent back is two events. A restart doesn't replay
  them all, as `Jobs` keeps a snapshot of its board every 100 events (`snapshot every 100;`) and replays only those
  since, but nothing removes the old ones from the store.
- **One coordinator.** It does little work (it hands out chunks and adds up samples), so one goes a long way, but it's
  the one pod that can't be lost mid-job.
- **The model is simple:** independent holdings with normally distributed daily returns. A real VaR would use
  correlated returns (or history), more horizons and more instruments. The grid side wouldn't change.

## What it found in Piper

Nothing needed fixing: it compiled and its tests passed as first written, on features the earlier apps had fixed.
One thing shaped the design. An `actor` can only hold fields and `on` handlers, with no helper functions of its own,
so the rules went into top-level functions over an immutable board. That turned out better, as they can be tested
without actors.
