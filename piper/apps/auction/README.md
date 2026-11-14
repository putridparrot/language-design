# Auction: bidding live in the browser

An auction site written in Piper, to prove its way of writing web apps: pages as markup, updated in part by htmx,
and pushed to by the server as things change, with no JavaScript written. People list lots, browse and search them,
and bid as the clock runs down; every page watching a lot sees each bid, and its countdown, as they happen.

It's the proving app of the web UI plan in [docs/future.md](../../docs/future.md#web-ui), as the
[tracker](../tracker/README.md) was for APIs.

## Starting and stopping it

From the `piper` folder, with a `piper` tool built from this repository (see
[the tracker's README](../tracker/README.md#getting-the-piper-tool)):

```
piper run apps/auction                    # http://localhost:5090, with three lots to bid on
piper run apps/auction -- --port 6000     # on another port
piper run apps/auction -- --empty         # with no lots
piper run apps/auction --watch            # restart on each save, reloading the open pages
piper test apps/auction                   # its tests
```

**Stopping it:** Ctrl+C. Nothing is stored: the lots and their bids live in memory, and go when it stops.

**Trying it:** open a lot in two browser windows, bid in one, and watch the other change. Bid in one window under one
name, then outbid yourself from the other under another name: the first is told it's been outbid. A lot closes in its
last 30 seconds, when each bid keeps it open 30 seconds more.

## How it works

```
 browser                                  server
 ───────                                  ──────
 GET /lots/7          ─────────────────▶  LotPage(lot): markup, in the layout
 EventSource /_piper/live?topic=lot-7 ─▶  (watching the lot)
 Bid (htmx post)      ─────────────────▶  Form<NewBid> → lot 7's actor: Bid(bid, now)
                      ◀── bid panel ────     one bid at a time: checked, taken, and then
 price, bids, status  ◀── pushed ───────     Broadcast → Live.Publish("lot-7", "price", …)
```

- **The rules** ([domain/lots.piper](domain/lots.piper)): a bid must meet the starting price, then beat the last by
  £1 (£5 from £100). A lot's life is a `statemachine`: open; closing in its last 30 seconds, when each bid keeps it
  open 30 seconds longer, so no one wins by bidding in the last second; then sold, if the highest bid meets the reserve,
  or unsold. `NewLot` and `NewBid` say what's wrong with them for people (`requires … else "…"`), which the forms
  show beside their fields.
- **The actors** ([actors/auction.piper](actors/auction.piper)): each lot is an actor holding its life, so bids
  arriving at once, from every page, are taken one at a time, each checked against the one before: no two people can
  lead at once, and no bid is lost. The auctioneer actor keeps the catalogue and passes the time on to the lots, once
  a second. A lot says what changed through a function it's given (`(LotChange) => unit`), so the domain doesn't know
  about the web.
- **The pages** ([web/pages.piper](web/pages.piper)): markup, in components (`Price`, `Status`, `Bids`, `BidPanel`)
  used both to draw a page and to push its parts. Links are typed routes (`SiteRoutes.Lot(id)`), the forms are read
  into records (`Form<NewLot>`, their inputs checked against the record by `<form for={NewLot}>`), and the lot list
  searches as you type (`hx-get`).
- **Live** ([web/live.piper](web/live.piper)): `Broadcast` turns each change into pushed fragments: to the lot's
  page (its price, bids and status), to the lot list (its row), and to the bidder who was outbid (on a topic of their
  own, which their bid panel starts watching once they bid).
- **The endpoints** ([web/site.piper](web/site.piper)): in a group with a layout, so a browser gets whole pages and
  htmx gets fragments, from the same handlers.

The tests check the rules and the state machine with a fixed clock; 400 bids at once on one lot (each taken beats the
last, and the leader made the highest); a property test, that whatever bids come, from any bidder, at any time and in
any order, bids never fall, the highest leads, each refusal has its reason, and the lot sells only when the reserve
is met; being outbid; lots ending and refusing bids; and the app over HTTP: the list,
search, selling with errors, a bid through the form pushed to a page watching the lot, and a bid too low.

It was also tried in a browser (headless Edge, driven through its DevTools port): a page showed another bidder's bid
without being touched; a bid made by clicking the page's button was taken (its anti-forgery token sent by htmx as a
header); and being outbid was pushed to the bidder's page.

## What it doesn't do

- **Nothing is stored:** a restart loses every lot. Persisted actors, or the data plugin, would keep them.
- **One server:** the topics pages watch are the process's. Across pods, the lots would be remote actors (as in
  [apps/stock](../stock/README.md)), each publishing to the pages connected to every pod.
- **No accounts:** a bidder is the name they type. Sign-in would let the auction say "you" and stop anyone bidding as
  someone else.

## What it found in Piper

| Gap | What happened | Fixed |
|---|---|---|
| An actor's result type | `on Bid(…) => Result<LotView, BidRefusal> { if … { Err(…) } else { Ok(view) } }` didn't compile: `Err` and `Ok` couldn't infer their other type argument, as the handler's declared result didn't reach its body (it does a function's). | **Plugins:** the actors and remote actors plugins bind the body as the declared result, so `Ok`, `Err` and `None` infer from it. |
| A state machine's helpers | Taking a bid (three assignments) couldn't be a method of the `statemachine`, which has only data and states, so it's written in each transition that takes one. | **Plugin:** a machine can declare functions, its class's methods; `LotLife` takes a bid in `Take`. |
| `[None, None, Some(x)]` | A list literal whose `None`s come before its `Some` doesn't take their type from it, so needs a type annotation. | **Language:** elements with no type of their own take the others'. |
| `[NotNullIfNotNull]` | `WebUtility.HtmlDecode(text)` is a `string` for a `string`, as .NET says, but Piper makes it a `string?`. | **Language:** given a plain value, the result is one. |

Also found and fixed on the way to it (each in [CHANGELOG.md](../../CHANGELOG.md)): generic .NET methods' constraints
weren't checked; Piper.Runtime's `Option` and `Result` from .NET weren't Piper's; `[NotNullWhen(true)] out` values
weren't plain where the call returned true; and the forms' anti-forgery hook was shared by every app in a process.
