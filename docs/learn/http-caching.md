# Learn: HTTP caching — the layer *above* Redis

> **Weekday homework for Day 6.** In class we cache inside the app, with Redis. That is one caching layer. There are two more, and they sit *above* your server: the client, and the CDN. This guide is the reading; the code is already on your branch (`ETagFilterAttribute`, ADR-048). One page, one decision each.

Today's Redis cache answers *"do not ask Postgres the same question twice."* HTTP caching answers a bigger one: *"do not send the same bytes over the network twice."* The cheapest request is the one that never reaches your server.

There are four levers. Each is a real decision with a trade-off.

## 1. `Cache-Control` and `max-age` — let the client not ask at all

```
Cache-Control: public, max-age=60
```

You are telling the browser (and any proxy) *"this is good for 60 seconds — do not even call me during that window."* For a menu that changes rarely, that is a real win: the second page-load makes zero requests for that resource.

- **Trade-off:** you have now cached in a place you cannot invalidate. Once the browser has it, you cannot reach in and delete the key the way you delete a Redis key on write. The stale window is the full `max-age`, everywhere, and you cannot shorten it after the fact.
- **When:** static-ish, low-stakes reads. **Never** on anything you must be able to correct instantly — the same rule as *never cache order status*.

## 2. `ETag` and `If-None-Match` — ask, but skip the body

This is the one the branch implements. On the first response the server sends a fingerprint:

```
ETag: "a1b2c3"
```

Next time, the client sends it back:

```
If-None-Match: "a1b2c3"
```

If nothing changed, the server replies **`304 Not Modified`** with **no body**. The request still happens — so freshness is guaranteed — but the expensive part, serialising and shipping the payload, does not.

- **Trade-off vs `max-age`:** ETag keeps a round-trip (you still ask) but is always fresh; `max-age` skips the round-trip but can be stale. ETag is *"always correct, sometimes cheap"*; `max-age` is *"always cheap, sometimes wrong."*
- **On the branch:** see `src/Tadka.Api/Filters/ETagFilterAttribute.cs` and [ADR-048](../adrs/048-response-compression-conditional-get.md). Run a menu GET twice with `curl.exe -i` and watch the second one return `304`.

## 3. Compression — smaller bytes on the wire

```
Content-Encoding: gzip
```

Menu JSON compresses well (repeated field names). Turning on response compression can cut payload size several-fold for text.

- **Trade-off:** CPU on every response for bytes on the wire. Worth it for text over a network; pointless for already-compressed data (images, video) and pointless on localhost. It pairs with ETag: a `304` sends no body, so there is nothing to compress — the two together mean *"often no body at all, and when there is one, it is small."*

## 4. A CDN — cache at the edge, near the user

A CDN is the same idea as `Cache-Control`, but the cache lives in a hundred cities instead of one browser. A user in Delhi gets the menu from a Delhi edge node, never touching your Bangalore server.

- **Trade-off:** it is another system to run, another place data can be stale, and it only helps content that is the same for many users. Per-user or per-order data has no business at a shared edge — that is a data-leak waiting to happen, not a cache.
- **When:** images, static assets, and genuinely shared read-only content. On the Week-8 Swiggy teardown the CDN is the box in front of restaurant images, not in front of order status.

## The one-picture summary

```
Browser  ──Cache-Control/max-age──▶  never asks (fastest, cannot invalidate)
   │
   ├──────If-None-Match/ETag──────▶  asks, gets 304, no body (always fresh)
   ▼
  CDN     ──edge cache──▶  shared read-only content only, near the user
   ▼
 Your app ──Redis cache-aside──▶  today's class: do not ask Postgres twice
   ▼
 Postgres
```

Every layer is the same trade you drew on the board today: **staleness against work.** The higher up you cache, the cheaper the hit and the harder it is to invalidate. That is why order status is cached at *none* of these layers, and why a menu can safely live at several.

## Homework deliverable

Pick one Tadka endpoint. In half a page:

1. Which of the four layers would you apply, and which would you refuse?
2. For each one you apply, what is your stale window, and what breaks inside it?
3. One sentence: why is order status different from the menu at *every* layer?

No code required. The reasoning is the deliverable — same as every week.
