# Stock: actors across pods

A stock service written in Piper, to show the remote actors plugin running as several nodes: separate processes,
Docker containers, or pods in Kubernetes (AKS).

Each product is an actor: its count lives only in that actor, which handles one message at a time. The nodes form a
cluster, and the products are spread over them, each kept by one, its owner, and moved to another when a node stops,
fails or joins. Every node serves the same HTTP API and answers for any product by asking the product's actor over the
network (HTTP/2), wherever it is. So many clients reserving the same product through every node at once can't oversell
it, with no locks and no database transaction.

```
                     load balancer (one address)
             ┌──────────────┼──────────────┐
             ▼              ▼              ▼
        stock-0          stock-1          stock-2        each: HTTP API on 8080,
        warehouse        warehouse        warehouse      actors on 8000 (HTTP/2)
        Product/bolt                      Product/gear
             ▲  "reserve 1 gear"  ───────────▶ │
             └──────────── a reservation ◀─────┘
                              │
                      PostgreSQL: every product's events, shared
```

## How it works

- **The cluster:** each node holds a lease in the database the nodes share (`Cluster__Provider=postgres`, the data
  plugin's `DatabaseClusterMembership`), so they find each other with no list of nodes; or joins through seed nodes
  (`--nodes`, some or all of them) when there's no shared database. The nodes ping each other
  every second, so each knows which are up; one unheard for five seconds is down
  ([actors/shards.piper](actors/shards.piper), on Piper.Actors.Remote's `Cluster`).
- **Which node keeps a product:** its owner, by rendezvous hashing over the nodes that are up, so every node agrees,
  and a node joining or leaving moves only the products it gains or loses. `Product.InCluster(node, sku)` is a handle
  to it wherever it is: each message goes to the owner, which starts the product's actor as the first one arrives
  ([actors/products.piper](actors/products.piper)), from its events.
- **Moving:** when the nodes change, each stops the products it no longer keeps, and their next messages start them on
  their new owners, carrying on from their events in the database the nodes share. A node that's stopped hands its
  products over first; one that crashes is found down, and its products start elsewhere. While the nodes disagree (a
  split network), two may each start a product; the store refuses the second writer, which stops, so a count is never
  written twice.
- **The API** ([api/api.piper](api/api.piper)): any node answers for any product. Each answer says which node served
  it (`servedBy`) and which node keeps the product (`result.node`).

  | Request | What it does |
  |---|---|
  | `GET /` | What can be asked, as text, and which node answered |
  | `GET /stock/{sku}` | The product's level |
  | `POST /stock/{sku}/receive?quantity=10` | Adds to it |
  | `POST /stock/{sku}/reserve?quantity=1` | Takes from it, or 409 if there aren't enough |
  | `GET /nodes` | This node, the nodes up, and the products this node keeps |
  | `GET /cluster` | Every node it knows, whether it answered, and the products each keeps, with their levels |
  | `GET /dashboard` | All of that in a browser, with buttons to try it (see below) |
  | `GET /healthz` | "Healthy" while the node runs: Kubernetes' liveness probe |
  | `GET /ready` | "Healthy" once the node has joined the cluster, else 503: its readiness probe |

  If a product's node doesn't answer (it crashed, and isn't yet found down), the answer is a 503 naming it, not a 500. Every answer
  closes its connection, so a client's next request can go to another node behind a load balancer.
- **The dashboard** ([api/dashboard.piper](api/dashboard.piper)), at `/dashboard` on any node: a card per node,
  showing whether it's up, the products it keeps, and how many of the page's requests it served. The card of the node
  that answered your last request is outlined (the page's own refreshes, every two seconds, don't move it). You can stock or reserve a product, and run the load demonstration from the browser.
  Delete a pod while it's open to watch its products move to the others, with their counts, and back when it returns.
- **The demonstration** ([load/load.piper](load/load.piper)): `load URL…` stocks a new product, then sends many
  reservations of one unit at once, shared among the URLs. Each request opens its own connection, so a load balancer
  spreads them over the pods. It reports how many were granted, which nodes served them, and whether every unit was
  reserved exactly once.

## Running it

From the `piper` folder, with a `piper` tool built from this repository (see
[the tracker's README](../tracker/README.md#getting-the-piper-tool)).

**As three processes,** one per terminal, sharing a SQLite file for their products' events (see *Keeping the counts*
below), which they also find each other through, each holding a lease there, so none is told about the others (in
PowerShell, set `$env:Events__Provider` and the others first):

```
export Events__Provider=sqlite Events__Connection="Data Source=stock.db"
export Cluster__Provider=sqlite Cluster__Connection="Data Source=stock.db"
piper run apps/stock -- --port 8001 --http 8081
piper run apps/stock -- --port 8002 --http 8082
piper run apps/stock -- --port 8003 --http 8083

piper run apps/stock -- load http://127.0.0.1:8081 http://127.0.0.1:8082 http://127.0.0.1:8083
```

Stop one (Ctrl+C) and its products carry on, with their counts, on the other two; start it again and they come back.
A fourth node (`--port 8004 --http 8084`) joins the same way and takes its share. Without a shared database, list seed
nodes instead: `--nodes 127.0.0.1:8001,127.0.0.1:8002`, some or all of them.

```
500 reservations of widget-a43378, with 100 in stock:
  granted 100, refused 400 (none left), failed 0
  kept by 127.0.0.1:8002
  served by:
    127.0.0.1:8001: 167
    127.0.0.1:8002: 167
    127.0.0.1:8003: 166
  afterwards: 0 on hand, 100 reserved
  every unit reserved exactly once: none oversold
```

`--stock`, `--requests` and `--concurrency` change the run (`-- --help` lists them all).

**In Docker,** as three containers that reach each other by name, as pods do:

```
docker compose -f apps/stock/docker-compose.yml up --build
piper run apps/stock -- load http://localhost:8081 http://localhost:8082 http://localhost:8083
# or from a container on their network, with no piper needed:
docker run --rm --network stock_default stock:local load http://stock-0:8080 http://stock-1:8080 http://stock-2:8080
```

The image is built from the `piper` folder, since it compiles the app with this repository's compiler:
`docker build -f apps/stock/Dockerfile -t stock .` With piper installed, `piper build apps/stock --publish` writes
`apps/stock/bin/publish`, a folder ready for `docker build -t stock apps/stock/bin/publish`, built from the compiled
app with no SDK in the image.

**`piper test apps/stock`** starts a cluster of three nodes in one process, and checks that every node agrees on who
keeps a product, that a node cut off or stopped has its products served by the others, that reservations from all
three never oversell, and that the load run through their APIs reserves each unit exactly once.

**Stopping it:**
- **Processes:** Ctrl+C in each terminal.
- **Docker Compose:** Ctrl+C, then `docker compose -f apps/stock/docker-compose.yml down` to remove the containers
  and their network. Add `--rmi local` to remove the `stock:local` image as well.

**Keeping the counts:** each product keeps its count as events (a [persisted actor](../../docs/plugins.md#keeping-state-persisted-actors)).
Where they're kept is configuration, not code (`Events:Provider` and its settings, from appsettings.json or the
environment), and **every node must share it**, as a product moves between them: wherever its actor starts, it
replays its events. With none, a node keeps them in memory, which suits a single node only. On one machine, a SQLite
file the nodes share does (made if it isn't there, by whichever node opens it first). Docker Compose and the Kubernetes
Deployment use PostgreSQL (`postgres`, its package in piper.json); a database in your cloud is only other settings:
`postgres` (Amazon RDS and Aurora, Google Cloud SQL, Azure's) or `sqlserver` (Azure SQL, Amazon RDS, with its EF Core
package added). To start again with nothing, delete the file (with Docker Compose, `down -v`).

**Seeing its traces:** each node exports traces, metrics and logs (the [telemetry plugin](../../docs/plugins.md#telemetry-opentelemetry))
when `OTEL_EXPORTER_OTLP_ENDPOINT` says where. .NET Aspire's dashboard takes them with no setup:

```
docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard
```

Start the nodes with `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` set (in PowerShell,
`$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"` first), then open http://localhost:18888, signing in with
the token the dashboard printed. A request is one trace: `POST /stock/{sku}/reserve` on the node it reached, then
`Product.Reserve` on the node that keeps the product, each span showing its node. That's what `servedBy` and the
dashboard page show by hand.

## On Docker Desktop's Kubernetes

The same manifest, in a namespace of its own, with the image built locally (Docker Desktop's Kubernetes uses Docker's
images). Its LoadBalancer is published on localhost:80, so port 80 must be free.

```
docker build -f apps/stock/Dockerfile -t stock:local .
kubectl --context docker-desktop create namespace stock
sed 's#REGISTRY/stock:1#stock:local#' apps/stock/k8s/stock.yaml | kubectl --context docker-desktop -n stock apply -f -
kubectl --context docker-desktop -n stock rollout status deployment/stock

piper run apps/stock -- load http://localhost
kubectl --context docker-desktop -n stock run stock-load --rm -i --restart=Never --image stock:local -- load http://stock

kubectl --context docker-desktop delete namespace stock      # clearing up
```

In PowerShell, the `sed` line is
`(Get-Content apps/stock/k8s/stock.yaml) -replace 'REGISTRY/stock:1', 'stock:local' | kubectl --context docker-desktop -n stock apply -f -`.

Then open http://localhost/dashboard. **Stopping it:** deleting the namespace removes everything in it, and frees
port 80. `docker image rm stock:local` removes the image too.

## On AKS

With the Azure CLI signed in, and names of your choosing in place of `stockdemo`. The registry name must be unique
across Azure.

```
az group create --name stockdemo --location uksouth
az acr create --resource-group stockdemo --name stockdemoacr --sku Basic
az aks create --resource-group stockdemo --name stockdemo --node-count 2 --attach-acr stockdemoacr --generate-ssh-keys
az aks get-credentials --resource-group stockdemo --name stockdemo

# Build the image in the registry, from the piper folder (no local Docker needed):
az acr build --registry stockdemoacr --image stock:1 --file apps/stock/Dockerfile .
```

Point the manifest at that image, and apply it. In bash:

```
sed 's#REGISTRY/stock:1#stockdemoacr.azurecr.io/stock:1#' apps/stock/k8s/stock.yaml | kubectl apply -f -
```

Or in PowerShell:

```
(Get-Content apps/stock/k8s/stock.yaml) -replace 'REGISTRY/stock:1', 'stockdemoacr.azurecr.io/stock:1' | kubectl apply -f -
```

[k8s/stock.yaml](k8s/stock.yaml) makes:
- **A Deployment of three pods.** Each is given `--host <its pod IP>`, which is how the others reach it, and 30
  seconds to hand its products over when it's stopped. No pod is told about the others.
- **`stock-db`,** PostgreSQL on a volume of its own, where every node keeps its products' events and its lease in the
  cluster (the `PiperMembers` table), with its password and connection in the `stock-db` Secret (change them; or
  point the Secret at your cloud's database instead).
- **`stock`,** a LoadBalancer in front of their HTTP APIs.

```
kubectl get pods -l app=stock                       # wait for 3/3 Running and ready
kubectl get service stock                           # its EXTERNAL-IP, once Azure has given it one
kubectl logs deployment/stock                       # "Node 10.244.0.12:8000, one of 3 up, …"

piper run apps/stock -- load http://<EXTERNAL-IP> --requests 1000
# or inside the cluster, from the same image:
kubectl run stock-load --rm -i --restart=Never --image stockdemoacr.azurecr.io/stock:1 -- load http://stock --requests 1000
```

Each reservation is served by whichever pod the load balancer picks, and all of them reach the one pod keeping the
product.

**Losing a pod:** `kubectl delete pod <one of them>`. It leaves the cluster as it stops, and its products carry on, with
their counts, on the other two. The Deployment starts a new pod (under a new IP), which takes a lease, joins, and
takes its share. A pod that crashes rather than stopping is found down within five seconds; until then, requests for
its products get a 503 naming it. Its lease runs out within thirty seconds, and the others soon forget it.

**Scaling:** `kubectl scale deployment stock --replicas 5`. The new pods find the others through the database, and
each takes its share of the products, which move with their counts.

**Clearing up:** `az group delete --name stockdemo`, which removes the cluster, the registry and everything in them.

## What it doesn't do

It's a demonstration of actors across pods, not a stock system.
- **One PostgreSQL pod,** which is fine for a demonstration; a real one would use the cloud's managed database.
- **A product moving is briefly unavailable:** asks for it wait (up to their timeout) while it stops on one node and
  starts on another, replaying its events. A product with a long history keeps a snapshot every so often
  (`snapshot every 100;`) to start faster.
- **The nodes' HTTP/2 between pods isn't encrypted,** which is usual inside a cluster's network, but a service mesh or
  TLS would be needed beyond it.

## What it found in Piper

| Gap | What happened | Fixed |
|---|---|---|
| A member named like a file | A `Products()` handler on an actor, calling a function in `products.piper`, was an internal compiler error. Top-level functions live in a C# class named after their file (`Products`), which the member hid inside the actor's class. | **Language:** a file's class name now avoids the program's member names too (`ProductsModule` here). |
| Setting a .NET `bool?` property | `request.Headers.ConnectionClose = true` was an internal compiler error: the `Option<bool>` was assigned without being converted to a `bool?`. | **Language:** it's converted, as an argument already was, so `true` and `None` both work. |
| `self` as a name | A field called `self` is "reserved for future use". | **App:** named `address`. Not a gap. |
| Nodes sharing one database | Three nodes started at once against one event store (needed once products move between them) raced to create its tables, and two crashed with "table already exists". | **Plugin:** DatabaseEventStore carries on when another process has just made the database or a table. |
