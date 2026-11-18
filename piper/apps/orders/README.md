# Orders

An order service whose fulfilment is a durable workflow, to use Piper's workflows as a real program would: an order
reserves its stock, waits for its payment (for half an hour, or however long is configured), then ships, or gives the
stock back if the payment doesn't come, or doesn't cover it. A restart at any point carries on where it was, without
reserving or shipping twice.

```
piper run apps/orders                        # http://localhost:5090, its database orders.db
piper test apps/orders                       # simulated: a day's payment window passes at once

curl -X POST "http://localhost:5090/api/stock/tea?quantity=5"
curl -X POST http://localhost:5090/api/orders -H "Content-Type: application/json" \
     -d '{"id": "o-1", "sku": "tea", "quantity": 2, "total": 10}'
curl -X POST http://localhost:5090/api/orders/o-1/payment -H "Content-Type: application/json" -d '{"amount": 10}'
curl http://localhost:5090/api/orders/o-1    # its status, outcome and steps
```

## How it works

- **The workflow** ([domain/orders.piper](domain/orders.piper)): `workflow Fulfil(inventory, window)`. Its steps
  with effects are activities (`activity reserved = inventory.Reserve(order);`), each run once and recorded; waiting
  for the payment is `flow.Signal<Payment>("paid", window)`, durable, giving None if the window passes. Its code is
  checked to replay the same (PIP0513), and its shape (input, result, steps in order) is recorded in
  [domain/Fulfil.shape.json](domain/Fulfil.shape.json), so a change running orders couldn't replay is a compile error.
- **Payments** arrive at a webhook (`POST /api/orders/{id}/payment`), published as a `PaymentReceived` message; the
  `PaymentsIn` consumer passes each on to its order's workflow as the `paid` signal, and dead-letters one for an order
  there isn't. Messages go over the in-memory transport, or RabbitMQ with `Messaging__Provider=rabbitmq`.
- **The stock** ([domain/inventory.piper](domain/inventory.piper)) is in SQLite through EF Core (the data plugin):
  each product's stock, the reservations held, the shipments made. Each operation is idempotent for its order, as an
  activity runs at least once.
- **The server** ([api/api.piper](api/api.piper)): `workflows engine: Fulfil(…) from Events(connection);` runs the
  workflows, their histories in the same database (resuming the unfinished as it starts), `bus from "Messaging";` and
  `consume PaymentsIn(engine);` the payments, and `endpoints Api` the HTTP API.
- **Settings:** `Orders:Connection` (`Data Source=orders.db`) and `Orders:PaymentWindowMinutes` (30), from
  appsettings.json or the environment (`Orders__Connection`).

## On Kubernetes

[deploy.piper](deploy.piper) says how it runs (the deploy plugin), and each build writes
[k8s/orders.yaml](k8s/orders.yaml) from it: one pod (`recreate`), its SQLite database on a 1Gi volume so an order
waiting for its payment survives the pod being replaced, a LoadBalancer on port 80, and a health probe.

```
piper build apps/orders --publish                 # the program and its Dockerfile, in apps/orders/bin/publish
docker build -t <registry>/orders:1 apps/orders/bin/publish && docker push <registry>/orders:1
sed 's#REGISTRY/orders:1#<registry>/orders:1#' apps/orders/k8s/orders.yaml | kubectl apply -f -
```

The image was run with the manifest's settings (`Orders__Urls`, `Orders__Connection` on a volume) and an order placed
and paid through it shipped.

## Tests

- **Simulated** ([domain/orders.tests.piper](domain/orders.tests.piper), `simulate`): a paid order ships and an unpaid
  one gives its stock back after the window (a day, passing at once); an underpaid one is cancelled and a large one
  rejected; payments come through the bus; and over forty runs, each paying the orders (in full, short, or not at
  all) at moments it chooses and restarting the service twice, the stock always adds up and nothing stays reserved.
- **Over HTTP** ([api/api.tests.piper](api/api.tests.piper)): the service served from memory, an order placed, paid
  through the webhook, and shipped.
- **By hand:** an order placed, the process killed while it waited for its payment, started again, and the payment
  sent: the order shipped, from where it was.
