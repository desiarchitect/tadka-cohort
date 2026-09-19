# AI engineering — awareness (through Day 8)

This is **not** a Tadka feature. You do not add an LLM to `POST /orders` or `:5240/payments`. Open the HTML version for a clickable glossary: [`ai-engineering.html`](ai-engineering.html).

**AI engineering** is building a **product around** a model: timeout, fallback, cost, evals, and **where the model must never run**. Same discipline as Polly and “do not cache order status.”

---

## 1. Words from zero

| Term | Meaning | Tadka hook |
|---|---|---|
| **AI** | Software that does something that used to need a human guess (language, images, ranking). | Broad label. |
| **ML** | A program **fitted on examples** instead of only `if/else`. | Your order **state machine** is not ML. It is explicit rules. |
| **Model** | The trained blob you **call**. This week you almost never train one. | Like calling the fake payment gateway — an external dep. |
| **LLM** | A **large language model**: predicts likely next text. Fluent ≠ true. | Can describe biryani. Can also invent ₹1. |
| **Prompt** | The text you send: instructions + data. | A function argument, not a spell. |
| **Token** | A chunk of text the model reads/writes. Bills and limits are in tokens. | Cost + timeout, like Slow gateway. |
| **Hallucination** | A fluent **wrong** answer with no source. | “Paneer tikka is ₹50 at Meghana” — check the menu JSON. |
| **Embedding** | A list of numbers for “similar meaning.” | Search “spicy rice” ≈ biryani. |
| **RAG** | **R**etrieval-**A**ugmented **G**eneration: fetch **your** data, then ask the model. | `GET` menu, put JSON in the prompt. |
| **Tool / function call** | The model asks **your** code to run an API. | Allowed: `GET /orders/{id}`. Forbidden: `POST /payments`. |
| **Eval** | A **test set**: question + expected behaviour. | 10 menu questions; fail if it invents a dish. |
| **AI engineering** | Everything around the call: timeout, fallback, cost, PII, evals, blast radius. | Polly + “don’t cache money.” |

---

## 2. Where it fits on Tadka (days 1–8)

| Piece | LLM? | Why |
|---|---|---|
| Order state machine (Created → Confirmed) | **No** | Correctness. Rules, not guesses. |
| Server-side pricing | **No** | Money. Hallucinated ₹ is a bug. |
| Payment charge (`:5240`) | **No** | PCI / money. Timeout + bulkhead already exist. |
| Bulkhead / Polly | **No** (but **same ideas**) | Timeout and fallback around a flaky dep. |
| Redis menu cache | **No** | Cache-aside is enough. |
| “What’s veg under ₹300?” | **Yes — RAG** | Read `GET …/menu`, then answer. |
| “Why is my order still Created?” | **Yes — explain GET** | Read `GET /orders/{id}`. Never charge. |
| SSE live status | **No** for the stream | Notification. LLM can *summarise* a GET. |

The model sits **beside** Ordering. It is **not** inside Payment.

---

## 3. Two use cases

### A. Menu Q&A (RAG)

1. `GET http://localhost:5224/api/v1/restaurants/{id}/menu` (Day 6 cache still applies).
2. Put **that JSON** in the prompt: “Answer only from this menu. If it is not there, say you don’t know.”
3. User: “veg under 300 at Meghana?”
4. If the model names a dish **not** in the JSON, **your eval failed** — even if the sentence sounds good.

**Forbidden:** changing prices, placing an order, calling Payment.

### B. Status explainer (read-only tool)

1. `GET http://localhost:5224/api/v1/orders/{id}` only.
2. Prompt: one sentence, only from that JSON.
3. After Day 8 Payment-down, status `Created` → “The order was accepted; the charge did not complete because Payment was down.”
4. **Forbidden:** `POST /payments`, `POST /orders`, anything that charges.

---

## 4. Try it yourself (three options)

Timeout **2 s**. If the model is slow or down, return **“I don’t know.”** Same as Polly. Do not put the API key in a browser page.

### Option 1 — Stub (no model, class-safe)

Use [`ai-engineering.html`](ai-engineering.html): it answers from a **hardcoded** Meghana snippet. No key.

### Option 2 — Local (Ollama)

```powershell
# after Ollama is installed and a small model is pulled
curl.exe -s http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu -o menu.json
# then POST menu.json + your question to http://localhost:11434/api/generate
# timeout 2s; on failure print "I don't know"
```

### Option 3 — Hosted API (server-side)

Any OpenAI-compatible HTTP API. Key in env, **server only**. Sketch: GET Tadka menu → POST `{ model, messages: [system: "only this JSON", user: question] }` → show text. 2 s timeout.

---

## 5. What this is not

Calling a chatbot from `OrdersController` and shipping it. No eval, no timeout, LLM on the charge path. That is not AI engineering.

Day 10 (JWT/PII) will make logging prompts even more dangerous. Do not log addresses or card-shaped strings.
