# Stock: actors across pods

A stock service written in Piper, to show the remote actors plugin running as several nodes: separate processes,
Docker containers, or pods in Kubernetes (AKS).

Each product is an actor: its count lives only in that actor, which handles one message at a time. The products are
spread over the nodes, each kept by one of them. Every node serves the same HTTP API and answers for any product by
asking the product's actor over gRPC, wherever it is. So many clients reserving the same product through every node at
once can't oversell it, with no locks and no database transaction.

```
                     load balancer (one address)
             ┌──────────────┼──────────────┐
             ▼              ▼              ▼
        stock-0          stock-1          stock-2        each: HTTP API on 8080,
        warehouse        warehouse        warehouse      actors on 8000 (gRPC)
        product-bolt                      product-gear
             ▲  "reserve 1 gear"  ───────────▶ │
             └──────────── a reservation ◀─────┘
```

## How it works

- **Which node keeps a product:** every node is given the same list of nodes, in the same order (`--nodes`). A product
  belongs to the node at `hash(code) % count` ([actors/shards.piper](actors/shards.piper)). The hash is FNV-1a, since
  .NET's `string.GetHashCode` differs between processes.
- **Starting a product's actor:** each node hosts a `Warehouse` actor, which starts a `Product` actor the first time
  one of its products is asked for ([actors/products.piper](actors/products.piper)). As an actor, it starts each
  product once, however many nodes ask at the same moment.
- **The API** ([api/api.piper](api/api.piper)): any node answers for any product. Each answer says which node served
  it (`servedBy`) and which node keeps the product (`result.node`).

  | Request | What it does |
  |---|---|
  | `GET /` | What can be asked, as text, and which node answered |
  | `GET /stock/{sku}` | The product's level |
  | `POST /stock/{sku}/receive?quantity=10` | Adds to it |
  | `POST /stock/{sku}/reserve?quantity=1` | Takes from it, or 409 if there aren't enough |
  | `GET /nodes` | This node, all of them, and the products this node keeps |
  | `GET /cluster` | Every node, whether it answered, and the products each keeps, with their levels |
  | `GET /dashboard` | All of that in a browser, with buttons to try it (see below) |
  | `GET /healthz` | "ok", for Kubernetes' probes |

  If a product's node doesn't answer (stopped, or restarting), the answer is a 503 naming it, not a 500. Every answer
  closes its connection, so a client's next request can go to another node behind a load balancer.
- **The dashboard** ([api/dashboard.piper](api/dashboard.piper)), at `/dashboard` on any node: a card per node,
  showing whether it's up, the products it keeps, and how many of the page's requests it served. The card of the node
  that answered your last request is outlined (the page's own refreshes, every two seconds, don't move it). You can stock or reserve a product, and run the load demonstration from the browser.
  Delete a pod while it's open to watch the node go down and come back empty.
- **The demonstration** ([load/load.piper](load/load.piper)): `load URL…` stocks a new product, then sends many
  reservations of one unit at once, shared among the URLs. Each request opens its own connection, so a load balancer
  spreads them over the pods. It reports how many were granted, which nodes served them, and whether every unit was
  reserved exactly once.

## Running it

From the `piper` folder, with a `piper` tool built from this repository (see
[the tracker's README](../tracker/README.md#getting-the-piper-tool)).

**As three processes,** one per terminal:

```
piper run apps/stock -- --port 8001 --http 8081 --nodes 127.0.0.1:8001,127.0.0.1:8002,127.0.0.1:8003
piper run apps/stock -- --port 8002 --http 8082 --nodes 127.0.0.1:8001,127.0.0.1:8002,127.0.0.1:8003
piper run apps/stock -- --port 8003 --http 8083 --nodes 127.0.0.1:8001,127.0.0.1:8002,127.0.0.1:8003

piper run apps/stock -- load http://127.0.0.1:8081 http://127.0.0.1:8082 http://127.0.0.1:8083
```

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
`docker build -f apps/stock/Dockerfile -t stock .`

**`piper test apps/stock`** starts three nodes in one process, on free ports, and checks that every node agrees on
who keeps a product, that reservations from all three never oversell, and that the load run through their APIs
reserves each unit exactly once.

**Stopping it:**
- **Processes:** Ctrl+C in each terminal.
- **Docker Compose:** Ctrl+C, then `docker compose -f apps/stock/docker-compose.yml down` to remove the containers
  and their network. Add `--rmi local` to remove the `stock:local` image as well.

Nothing is kept on disk, so there's no data to clear: stopping a node loses its products' counts.

## On Docker Desktop's Kubernetes

The same manifest, in a namespace of its own, with the image built locally (Docker Desktop's Kubernetes uses Docker's
images). Its LoadBalancer is published on localhost:80, so port 80 must be free.

```
docker build -f apps/stock/Dockerfile -t stock:local .
kubectl --context docker-desktop create namespace stock
sed 's#REGISTRY/stock:1#stock:local#' apps/stock/k8s/stock.yaml | kubectl --context docker-desktop -n stock apply -f -
kubectl --context docker-desktop -n stock rollout status statefulset/stock

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
- **A StatefulSet of three pods,** `stock-0` to `stock-2`. Each is given `--host <its name>.stock-nodes` and the same
  `--nodes` list.
- **`stock-nodes`,** a headless service giving each pod a DNS name, which is how the nodes reach each other.
- **`stock`,** a LoadBalancer in front of their HTTP APIs.

```
kubectl get pods -l app=stock                       # wait for 3/3 Running and ready
kubectl get service stock                           # its EXTERNAL-IP, once Azure has given it one
kubectl logs stock-1                                # "Node stock-1.stock-nodes:8000 (node 2 of 3), …"

piper run apps/stock -- load http://<EXTERNAL-IP> --requests 1000
# or inside the cluster, from the same image:
kubectl run stock-load --rm -i --restart=Never --image stockdemoacr.azurecr.io/stock:1 -- load http://stock --requests 1000
```

Each reservation is served by whichever pod the load balancer picks, and all of them reach the one pod keeping the
product.

**Losing a pod:** `kubectl delete pod stock-2`. While it's gone, requests for its products get a 503 naming it, and the
others' products carry on. The StatefulSet starts a new `stock-2` under the same name, and the next request for one
of its products starts that product again there, empty.

**Clearing up:** `az group delete --name stockdemo`, which removes the cluster, the registry and everything in them.

## What it doesn't do

It's a demonstration of actors across pods, not a stock system.
- **Counts live only in memory:** a pod that restarts loses its products' counts. Storing each product's events
  (Proto.Actor's persistence, or the data plugin) would fix that.
- **The nodes are a fixed list:** scaling means changing `replicas` and `--nodes` together and restarting, and since
  ownership is `hash % count`, most products then belong to another node, so they start again empty. Finding nodes as
  they come and go, and moving actors between them, is Proto.Actor's clustering. That's on Piper's list (see the remote
  actors plugin in [docs/plugins.md](../../docs/plugins.md)).
- **gRPC between pods isn't encrypted,** which is usual inside a cluster's network, but a service mesh or TLS would
  be needed beyond it.

## What it found in Piper

| Gap | What happened | Fixed |
|---|---|---|
| A member named like a file | A `Products()` handler on an actor, calling a function in `products.piper`, was an internal compiler error. Top-level functions live in a C# class named after their file (`Products`), which the member hid inside the actor's class. | **Language:** a file's class name now avoids the program's member names too (`ProductsModule` here). |
| Setting a .NET `bool?` property | `request.Headers.ConnectionClose = true` was an internal compiler error: the `Option<bool>` was assigned without being converted to a `bool?`. | **Language:** it's converted, as an argument already was, so `true` and `None` both work. |
| `self` as a name | A field called `self` is "reserved for future use". | **App:** named `address`. Not a gap. |
