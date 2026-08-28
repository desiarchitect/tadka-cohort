# Where request data goes

Path, query, body, header. Four slots. One question: **what is this datum for?**

Read next to [`openapi-v1.yaml`](openapi-v1.yaml) and [`contract-reasoning.md`](contract-reasoning.md). Headers vs body (envelope vs letter) is taught live on Day 3. This page is the rest: **path vs query vs body**, plus when the same name (`customerId`) belongs in two different slots.

Day 3 examples only. `Idempotency-Key` is named as a header because Day 4 will put it there. Do not send it on Day 3.

---

## The rule

| Slot | The datum answers | Typical methods | In Tadka Day 3 |
|---|---|---|---|
| **Path** | *Which* resource am I talking about? Required to name the thing. Shows up in logs, bookmarks, cache keys. | GET, PATCH, POST on a sub-resource | `/restaurants/{id}`, `/orders/{id}`, `/restaurants/{id}/menu/{itemId}` |
| **Query** | *Which view* of a collection? Optional. Same resource family. Visible on GET, so it can be shared and cached. | GET lists | `?city=`, `?page=&pageSize=`, `?vegOnly=`, `?category=`, `?customerId=` on **list** orders |
| **Body** | *What* is the representation or intent? Create, partial update, or a named action. | POST, PATCH | Place order (no price). `PATCH { isActive: false }`. `POST .../cancel` `{ reason }` |
| **Header** | *This request*, not the resource. Envelope. Intermediaries read it without opening JSON. | Any | `Content-Type`, `Authorization`. Tomorrow: `Idempotency-Key` |

If you cannot say which row the value belongs in, it does not belong in the contract yet.

---

## Path parameters

Use a path segment when the request **does not make sense** without that identity.

```
GET  /api/v1/restaurants/{id}
GET  /api/v1/restaurants/{id}/menu
PATCH /api/v1/orders/{id}/status
```

- `{id}` is the restaurant or order you are acting on.
- Nested `{id}/menu/{itemId}` means: this dish, **of this restaurant**. A menu item is not a first-class collection on Day 3.

**Not path:** `page`, `city`, `vegOnly`. Those do not name a different resource. They change the *window* on the same list.

**Rejected:** `/api/v1/orders/page/2`. Pagination is not a resource.

---

## Query parameters

Use query when the client is still talking to the **same collection**, but wants a subset or a page.

```
GET /api/v1/restaurants?city=Bangalore&page=1&pageSize=10
GET /api/v1/restaurants/{id}/menu?vegOnly=true&category=Biryani
GET /api/v1/orders?customerId=c1b2c3d4-0001-4000-8000-000000000001
```

- Optional. Omit `city` and you get all restaurants (paged).
- Safe on **GET**: the full URL is the cache key and the shareable link.
- `pageSize` is clamped 1–50 in code so a client cannot dump the table.

**Not query:** the new order's items and address. That is a create. GET with a giant query string is not a create.

**Not query:** secrets (tokens, passwords). Query strings land in access logs, browser history, and `Referer`. Those go in a header (or body for a POST login you do not have on Day 3).

**Not query:** a 50-field JSON document. URLs have practical length limits. Intermediaries truncate. That used to mean “stuff it in POST.” HTTP now has a method for that case. See **QUERY** below. Day 3 lists are still GET + query.

---

## QUERY (RFC 10008, June 2026): search that is not GET and not POST

HTTP gained a new general-purpose method in June 2026: **`QUERY`** ([RFC 10008](https://www.rfc-editor.org/rfc/rfc10008.html)). Early drafts used the name SEARCH. The published method is QUERY.

**The hole it fills:** GET is safe and cacheable, but the criteria live in the URL (length limits, logs, no real JSON). POST can carry a body, but it is not safe: retries, caches, and browsers must assume it might change state. For twenty years APIs faked search as `POST /restaurants/search`. That is a read wearing a write verb.

| | GET | QUERY | POST |
|---|---|---|---|
| Safe (read-only intent) | Yes | Yes | No |
| Idempotent (retry is the same question) | Yes | Yes | No |
| Request body | No defined meaning | Expected (the query) | Expected |
| Cacheable | Yes | Yes | Not as a search |

`QUERY /api/v1/restaurants` with a JSON body is “ask this collection a question.” `200` with the matching rows. No new restaurant. Repeat the same QUERY and you get the same answer, so a CDN or retry library may replay it.

**When GET + query is still right (Day 3):** one or two optional filters, short values, shareable URL.

```
GET /api/v1/restaurants?city=Bangalore&page=1
GET /api/v1/restaurants/{id}/menu?vegOnly=true
```

That is a *view*, not a search product. Bookmarkable. Fine.

**When QUERY is the honest slot:** the criteria do not fit a query string, or you need structured JSON (geo radius, many optional facets, full-text `q`, “open now AND veg AND rating > 4”). Then the *question* belongs in the **body**, and the method must still say “this is a read.” That is QUERY, not POST.

**When POST /search is a lie:** you are not creating a resource. You are asking. POST tells every intermediary “this might have side effects.” Do not teach that as the default once QUERY exists.

**Tadka Day 3 does not implement QUERY.** `city`, `vegOnly`, `category`, `page` stay query params. We have not earned a search product (and we do not have PostGIS nearby-search). Revisit when “find restaurants” is more than `?city=` — that is the same discipline as not installing Kafka on Day 1.

**Ecosystem catch-up:** browsers and many HTTP clients are still growing QUERY support. Until your client and any proxy in front of you understand the method, you may have to keep GET or a documented POST fallback. Name that as a trade-off, not as “POST is how search works.”

---

## Body

Use the body when you are sending a **representation** or an **intent**.

```
POST /api/v1/orders          { customerId, restaurantId, items, deliveryAddress }
PATCH /api/v1/restaurants/{id}   { isActive: false }
POST /api/v1/orders/{id}/cancel  { reason }
```

- **POST create:** server assigns the id. Body is “what to create”, not the id in the path.
- **PATCH:** only the fields that change. Omitted means “leave it”. That is why we do not use PUT.
- **POST action:** cancel is a capability with a reason, not `PATCH { status: "Cancelled" }`.

**GET has no body.** Caches and many proxies ignore or mishandle GET bodies. If you need an id to read, put it in the **path**. If you need a filter, put it in the **query**.

**Rejected:** `GET /api/v1/orders` with `{ "id": "..." }` in JSON. Then you cannot bookmark, you cannot cache, and every HTTP logger looks at an empty query.

**Rejected:** price or `totalAmount` in the place-order body. That is not “where data goes”; that is **trust**. See `contract-reasoning.md`. Price is not a path or query either.

---

## Headers (pointer, not a second lecture)

Envelope: *how* this request is sent, not *what* the order is.

Day 3 live: `Content-Type`, `Content-Length`, `Host`, `Authorization`.  
Day 4: `Idempotency-Key` is about the **request**, so it is a header, not `POST /orders` JSON and not a column on `Order`.

If you put request metadata in the body, gateways must parse JSON to route or reject. That is the middlebox tax.

---

## Same name, two jobs

`customerId` appears twice on Day 3. That is not sloppiness.

| Where | Job |
|---|---|
| **Body** of `POST /orders` | Who is placing **this** order. Part of the new resource. |
| **Query** of `GET /orders` | Filter the collection: “orders for this customer”. Optional. Same list resource. |

`restaurantId` in the **body** of `POST /orders` is a *reference* to another context (no cross-schema FK). It is not the path of the order. The order’s path id is assigned by the server and returned in `201`.

Path `{id}` on `PATCH /orders/{id}/status` is the order you are moving. The new status is **body**, because it is the change, not the identity.

---

## Nest the parent in the path, not as a filter (Day 3 choice)

```
GET /api/v1/restaurants/{id}/menu
```

not

```
GET /api/v1/menu-items?restaurantId={id}
```

A dish without a restaurant is not a Tadka resource in this slice. Nesting says that. Query `restaurantId` would make menu items look like a global table you filter. We did not design that.

Filters *on* that menu (`vegOnly`, `category`) stay query: they do not change whose menu it is.

---

## Rejected designs (one line)

| Design | Why not |
|---|---|
| `POST /getOrder` | Verb in the path. GET + path id. |
| `GET /orders` + JSON `{ id }` | Identity belongs in the path. GET has no body. |
| `GET /orders?id=` | Works, but `{id}` in the path is the resource. Query `id` looks like a filter on a list. Use path for one, query `customerId` for many. |
| `page` in the path | Pagination is a view, not a child resource. |
| Price in query or body on place-order | Server prices. Trust, not placement trivia. |
| `POST /restaurants/search` as the default | A read wearing POST. RFC 10008 QUERY is the method for a body that is still a question. Day 3 still uses GET `?city=`. |
| Token in query | Logs and referrers. Header. |
| `DELETE` with a JSON body | We do not DELETE. Deactivate with PATCH body `{ isActive: false }`. |

---

## Quick check

Before you add a field, say out loud:

1. If I remove it, can I still *name* the resource? If no → **path**.
2. Is it optional and only changing which rows I *see*? → **query** (and the method is GET).
3. Is it the content of a create, patch, or action? → **body**.
4. Is it about this HTTP call (auth, content type, replay key)? → **header**.
5. Is it a **read** whose criteria will not fit a URL? → method **QUERY**, criteria in the **body**. Not POST. Not Day 3.

If two answers fire, you have two jobs. Use two slots (`customerId` body vs query). Do not mash them into one.
