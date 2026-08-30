// Tadka Demo Console - vanilla JS, no build step.
// Menu + Pay (idempotency lever) + payment status + SSE tracking + delivery map.
// Uses fetch() for SSE so we can send Authorization (EventSource cannot).

const API = window.location.origin;

const OWNER_EMAIL = "owner1@tadka.test";
const DEMO_PASSWORD = "Password123!";

const FLOW = [
  "Created",
  "Confirmed",
  "Preparing",
  "ReadyForPickup",
  "PickedUp",
  "Delivered",
];

const MAP_BOUNDS = {
  minLat: 12.8,
  maxLat: 13.2,
  minLng: 77.4,
  maxLng: 77.8,
};

// Meghana Foods (seeded restaurant) - approximate Indiranagar coords
const RESTAURANT_PIN = { lat: 12.9784, lng: 77.6408, label: "Meghana" };
const DELIVERY_PIN = { lat: 12.9141, lng: 77.6411, label: "You" };

const state = {
  customerToken: null,
  ownerToken: null,
  orderId: null,
  streamAbort: null,
  events: [],
  mapPollId: null,
  riderPos: null,
  trackMeta: null,
  restaurants: [],
  menu: [],
  selectedRestaurantId: null,
  selectedItemId: null,
  sharedIdemKey: newKey(),
  paymentPollId: null,
};

const $ = (id) => document.getElementById(id);

function newKey() {
  return `console-${crypto.randomUUID()}`;
}

function show(el) {
  el.classList.remove("hidden");
}

function hide(el) {
  el.classList.add("hidden");
}

function setError(id, msg) {
  const el = $(id);
  if (!msg) {
    hide(el);
    el.textContent = "";
    return;
  }
  el.textContent = msg;
  show(el);
}

async function login(email, password) {
  const res = await fetch(`${API}/api/v1/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error(body.error || `Login failed (${res.status})`);
  }
  const data = await res.json();
  return data.accessToken;
}

// ---------- Menu ----------

function setInstancePill(res) {
  // Day 6 scale-out demo: the monolith stamps X-Tadka-Instance so the class can
  // see which replica answered. Absent on single-instance days - show n/a.
  const pill = $("instance-pill");
  const instance = res.headers.get("x-tadka-instance");
  pill.textContent = instance ? `instance: ${instance}` : "instance: n/a";
  pill.classList.toggle("live", !!instance);
}

async function loadRestaurants() {
  const res = await fetch(`${API}/api/v1/restaurants?page=1&pageSize=20`, {
    headers: state.customerToken ? { Authorization: `Bearer ${state.customerToken}` } : {},
  });
  if (!res.ok) throw new Error(`Restaurants failed (${res.status})`);
  const data = await res.json();
  state.restaurants = data.items || [];
  const sel = $("restaurant-select");
  sel.innerHTML = "";
  for (const r of state.restaurants) {
    const opt = document.createElement("option");
    opt.value = r.id;
    opt.textContent = r.name;
    sel.appendChild(opt);
  }
  if (!state.selectedRestaurantId && state.restaurants.length) {
    state.selectedRestaurantId = state.restaurants[0].id;
  }
  if (state.selectedRestaurantId) sel.value = state.selectedRestaurantId;
}

async function loadMenu() {
  if (!state.selectedRestaurantId) return;
  const res = await fetch(`${API}/api/v1/restaurants/${state.selectedRestaurantId}/menu`, {
    headers: state.customerToken ? { Authorization: `Bearer ${state.customerToken}` } : {},
  });
  if (!res.ok) throw new Error(`Menu failed (${res.status})`);
  setInstancePill(res);
  state.menu = await res.json();
  renderMenu();
}

function renderMenu() {
  const list = $("menu-list");
  list.innerHTML = "";
  const available = state.menu.filter((m) => m.isAvailable);
  if (!available.length) {
    list.innerHTML = `<p class="hint">No available items.</p>`;
    return;
  }
  if (!state.selectedItemId || !available.some((m) => m.id === state.selectedItemId)) {
    state.selectedItemId = available[0].id;
  }
  for (const item of available) {
    const row = document.createElement("label");
    row.className = "menu-item";
    const price = item.price ? `${item.price.currency} ${item.price.amount}` : "";
    row.innerHTML = `
      <input type="radio" name="menu-item" value="${item.id}" ${item.id === state.selectedItemId ? "checked" : ""} />
      <span class="menu-name">${item.name}</span>
      <span class="menu-price">${price}</span>`;
    row.querySelector("input").addEventListener("change", () => {
      state.selectedItemId = item.id;
    });
    list.appendChild(row);
  }
}

// ---------- Pay ----------

function idemHeaderFor(mode) {
  if (mode === "no-key") return null;
  if (mode === "same-key") return state.sharedIdemKey;
  return newKey(); // new-key
}

async function payOnce(mode) {
  const qty = Math.max(1, Math.min(9, parseInt($("qty").value, 10) || 1));
  const payload = {
    restaurantId: state.selectedRestaurantId,
    items: [{ menuItemId: state.selectedItemId, quantity: qty }],
    deliveryAddress: {
      line1: "Flat 402, Green Apartments",
      line2: "HSR Layout",
      city: "Bangalore",
      pincode: "560102",
      latitude: 12.9141,
      longitude: 77.6411,
    },
  };
  const headers = {
    "Content-Type": "application/json",
    Authorization: `Bearer ${state.customerToken}`,
  };
  const key = idemHeaderFor(mode);
  if (key) headers["Idempotency-Key"] = key;

  const res = await fetch(`${API}/api/v1/orders`, {
    method: "POST",
    headers,
    body: JSON.stringify(payload),
  });
  const bodyText = await res.text();
  let orderId = null;
  try {
    orderId = JSON.parse(bodyText).id || null;
  } catch {
    /* non-JSON error body */
  }
  return { status: res.status, orderId, key, body: bodyText };
}

function logTap(result, mode) {
  const ul = $("tap-log");
  const li = document.createElement("li");
  const time = new Date().toLocaleTimeString();
  const keyLabel = result.key ? result.key.slice(-8) : "none";

  // Classify the outcome so the break is unmissable on screen:
  // 201 + first-seen id  -> order created
  // 200 + known id       -> idempotent replay (the fix working)
  // 201 + second id      -> DUPLICATE (the break)
  const priorIds = [...ul.querySelectorAll("[data-order-id]")].map((n) => n.dataset.orderId);
  let cls = "ok";
  let verdict = "created";
  if (result.status === 200 && result.orderId && priorIds.includes(result.orderId)) {
    verdict = "replay - same order returned";
  } else if (result.status === 201 || result.status === 200) {
    if (result.orderId && priorIds.length && !priorIds.includes(result.orderId)) {
      cls = "dup";
      verdict = "DUPLICATE ORDER - customer pays twice";
    }
  } else {
    cls = "err";
    verdict = `failed (${result.status})`;
  }

  li.className = cls;
  if (result.orderId) li.dataset.orderId = result.orderId;
  li.innerHTML = `<span class="tap-time">${time}</span> <span>${result.status}</span> <span class="tap-id">${result.orderId ? result.orderId.slice(0, 8) : "-"}</span> <span class="tap-key">key:${keyLabel}</span> <strong>${verdict}</strong>`;
  ul.prepend(li);
}

async function pay(mode) {
  setError("order-error", null);
  try {
    const result = await payOnce(mode);
    logTap(result, mode);
    if ((result.status === 201 || result.status === 200) && result.orderId) {
      state.orderId = result.orderId;
      $("order-meta").textContent = `Order ${state.orderId}`;
      $("btn-track").disabled = false;
      connectSse(state.orderId, state.customerToken).catch((e) => {
        if (e.name !== "AbortError") setError("order-error", e.message);
      });
      startPaymentPolling();
    } else if (result.status >= 400) {
      setError("order-error", `Order failed (${result.status}): ${result.body.slice(0, 300)}`);
    }
  } catch (e) {
    setError("order-error", e.message);
  }
}

// ---------- Payment status ----------

function setPaymentPill(text, cls) {
  const pill = $("payment-pill");
  pill.classList.remove("live", "err", "warn-pill", "refund");
  pill.textContent = text;
  if (cls) pill.classList.add(cls);
}

function stopPaymentPolling() {
  if (state.paymentPollId) {
    clearInterval(state.paymentPollId);
    state.paymentPollId = null;
  }
}

async function pollPaymentOnce() {
  if (!state.orderId || !state.customerToken) return;
  try {
    const res = await fetch(`${API}/api/v1/payments/${state.orderId}`, {
      headers: { Authorization: `Bearer ${state.customerToken}` },
    });
    if (res.status === 404) {
      setPaymentPill("No payment yet", null);
      return;
    }
    if (res.status === 502 || res.status === 503) {
      setPaymentPill("Payment service down", "err");
      $("payment-meta").textContent = "Payment service unreachable - expected before Day 8, or during the Day 8/9 kill-the-service demos.";
      return;
    }
    if (!res.ok) throw new Error(`Payment lookup failed (${res.status})`);
    const p = await res.json();
    setError("payment-error", null);
    if (p.status === "Completed") {
      setPaymentPill("Completed", "live");
    } else if (p.status === "Failed") {
      setPaymentPill(`Failed - ${p.failureReason || "declined"}`, "err");
    } else if (p.status === "Refunded") {
      // Day 11 compensation payoff: restaurant rejected AFTER the charge - the saga refunds.
      setPaymentPill("Refunded", "refund");
    } else {
      setPaymentPill(p.status || "Pending", "warn-pill");
    }
    if (p.gatewayReference) {
      $("payment-meta").textContent = `Gateway ref: ${p.gatewayReference}`;
    }
    // Keep polling even after Completed - a refund can arrive later (Day 11 compensation).
  } catch (e) {
    setError("payment-error", e.message);
  }
}

function startPaymentPolling() {
  show($("payment-card"));
  stopPaymentPolling();
  pollPaymentOnce();
  state.paymentPollId = setInterval(pollPaymentOnce, 2000);
}

// ---------- Order status / SSE ----------

async function patchStatus(orderId, status, token) {
  const res = await fetch(`${API}/api/v1/orders/${orderId}/status`, {
    method: "PATCH",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({ status }),
  });
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`Status update failed (${res.status}): ${body}`);
  }
}

function formatTime(iso) {
  try {
    return new Date(iso).toLocaleTimeString();
  } catch {
    return iso;
  }
}

function renderTimeline() {
  const ul = $("timeline");
  ul.innerHTML = "";

  for (const step of FLOW) {
    const hit = state.events.filter((e) => e.status === step);
    const li = document.createElement("li");
    if (hit.length) li.classList.add("active");
    const last = hit[hit.length - 1];
    li.innerHTML = `
      <span class="dot"></span>
      <div>
        <div><strong>${step}</strong></div>
        ${last ? `<div class="time">${last.message} - ${formatTime(last.timestamp)}</div>` : `<div class="time">waiting...</div>`}
      </div>`;
    ul.appendChild(li);
  }

  // Non-standard statuses (e.g. Cancelled in the Day 11 refund demo) - append raw at bottom
  const extras = state.events.filter((e) => !FLOW.includes(e.status));
  for (const e of extras) {
    const li = document.createElement("li");
    li.classList.add("active");
    li.innerHTML = `<span class="dot"></span><div><strong>${e.status}</strong><div class="time">${e.message} - ${formatTime(e.timestamp)}</div></div>`;
    ul.appendChild(li);
  }
}

function setConn(status) {
  const pill = $("conn-pill");
  pill.classList.remove("live", "err");
  if (status === "live") {
    pill.textContent = "Connected";
    pill.classList.add("live");
  } else if (status === "err") {
    pill.textContent = "Error";
    pill.classList.add("err");
  } else {
    pill.textContent = "Disconnected";
  }
}

function pushEvent(evt) {
  state.events.push(evt);
  renderTimeline();
  if (evt.status === "Confirmed") startMapPolling();
  if (evt.status === "Delivered") stopMapPolling();
}

function latLngToCanvas(lat, lng, w, h) {
  const x = ((lng - MAP_BOUNDS.minLng) / (MAP_BOUNDS.maxLng - MAP_BOUNDS.minLng)) * (w - 24) + 12;
  const y = h - ((lat - MAP_BOUNDS.minLat) / (MAP_BOUNDS.maxLat - MAP_BOUNDS.minLat)) * (h - 24) - 12;
  return { x, y };
}

function drawMap() {
  const canvas = $("map-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const w = canvas.width;
  const h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = "#0b1220";
  ctx.fillRect(0, 0, w, h);

  ctx.strokeStyle = "#1e293b";
  ctx.lineWidth = 1;
  for (let i = 1; i < 4; i++) {
    const gx = (w / 4) * i;
    const gy = (h / 4) * i;
    ctx.beginPath();
    ctx.moveTo(gx, 0);
    ctx.lineTo(gx, h);
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(0, gy);
    ctx.lineTo(w, gy);
    ctx.stroke();
  }

  function dot(pin, color, radius) {
    const { x, y } = latLngToCanvas(pin.lat, pin.lng, w, h);
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(x, y, radius, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#94a3b8";
    ctx.font = "11px system-ui,sans-serif";
    ctx.fillText(pin.label, x + radius + 4, y + 4);
  }

  dot(RESTAURANT_PIN, "#f59e0b", 6);
  dot(DELIVERY_PIN, "#3b82f6", 6);
  if (state.riderPos) {
    dot({ ...state.riderPos, label: state.trackMeta?.riderName || "Rider" }, "#22c55e", 7);
    if (state.riderPos.lat && state.riderPos.lng) {
      ctx.strokeStyle = "rgba(34,197,94,0.35)";
      ctx.setLineDash([4, 4]);
      const r = latLngToCanvas(state.riderPos.lat, state.riderPos.lng, w, h);
      const d = latLngToCanvas(DELIVERY_PIN.lat, DELIVERY_PIN.lng, w, h);
      ctx.beginPath();
      ctx.moveTo(r.x, r.y);
      ctx.lineTo(d.x, d.y);
      ctx.stroke();
      ctx.setLineDash([]);
    }
  }
}

function setMapPill(text, live) {
  const pill = $("map-pill");
  if (!pill) return;
  pill.classList.remove("live", "err");
  pill.textContent = text;
  if (live) pill.classList.add("live");
}

async function fetchTrack(orderId, token) {
  const res = await fetch(`${API}/api/v1/deliveries/${orderId}/track`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (res.status === 404) return null;
  if (res.status === 502 || res.status === 503) return null;
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`Track failed (${res.status}): ${text}`);
  }
  return res.json();
}

function stopMapPolling() {
  if (state.mapPollId) {
    clearInterval(state.mapPollId);
    state.mapPollId = null;
  }
}

async function pollTrackOnce() {
  if (!state.orderId || !state.customerToken) return;
  try {
    const data = await fetchTrack(state.orderId, state.customerToken);
    if (!data) {
      setMapPill("No rider yet", false);
      $("map-meta").textContent = "Delivery service may be down (Days 6-7) or rider not assigned yet.";
      return;
    }
    state.trackMeta = {
      riderName: data.agentName || "Rider",
      status: data.status,
    };
    const loc = data.location;
    if (loc?.latitude != null && loc?.longitude != null) {
      state.riderPos = { lat: loc.latitude, lng: loc.longitude };
      setMapPill(`${state.trackMeta.riderName} - ${data.status}`, true);
      setError("map-error", null);
    } else {
      setMapPill(`${state.trackMeta.riderName} assigned`, false);
    }
    $("map-meta").textContent = `Polling ${API}/api/v1/deliveries/{orderId}/track every 2s`;
    drawMap();
    if (data.status === "Delivered") stopMapPolling();
  } catch (e) {
    setMapPill("Track error", false);
    $("map-pill")?.classList.add("err");
    setError("map-error", e.message);
  }
}

function startMapPolling() {
  const card = $("map-card");
  if (!card) return;
  show(card);
  stopMapPolling();
  drawMap();
  pollTrackOnce();
  state.mapPollId = setInterval(pollTrackOnce, 2000);
}

function parseSseChunk(buffer) {
  const frames = [];
  const parts = buffer.split("\n\n");
  const rest = parts.pop() ?? "";
  for (const part of parts) {
    if (!part.trim()) continue;
    let eventName = "message";
    let data = "";
    for (const line of part.split("\n")) {
      if (line.startsWith("event:")) eventName = line.slice(6).trim();
      if (line.startsWith("data:")) data += line.slice(5).trim();
    }
    if (data) frames.push({ eventName, data });
  }
  return { frames, rest };
}

async function connectSse(orderId, token) {
  if (state.streamAbort) state.streamAbort.abort();
  const ac = new AbortController();
  state.streamAbort = ac;
  state.events = [];
  renderTimeline();
  setConn("connecting");

  const res = await fetch(`${API}/api/v1/orders/${orderId}/events`, {
    headers: { Authorization: `Bearer ${token}`, Accept: "text/event-stream" },
    signal: ac.signal,
  });

  if (!res.ok) {
    setConn("err");
    const text = await res.text();
    throw new Error(res.status === 503 ? text : `SSE failed (${res.status}): ${text}`);
  }

  setConn("live");
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    const { frames, rest } = parseSseChunk(buffer);
    buffer = rest;
    for (const frame of frames) {
      try {
        const payload = JSON.parse(frame.data);
        pushEvent({
          status: payload.status || frame.eventName,
          message: payload.message || "",
          timestamp: payload.timestamp || new Date().toISOString(),
        });
      } catch {
        pushEvent({
          status: frame.eventName,
          message: frame.data,
          timestamp: new Date().toISOString(),
        });
      }
    }
  }
  setConn("disconnected");
}

function buildKitchenButtons() {
  const row = $("kitchen-buttons");
  row.innerHTML = "";
  const next = ["Confirmed", "Preparing", "ReadyForPickup", "PickedUp", "Delivered"];
  for (const s of next) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "ghost";
    b.textContent = s;
    b.addEventListener("click", async () => {
      if (!state.orderId || !state.ownerToken) return;
      setError("kitchen-error", null);
      try {
        await patchStatus(state.orderId, s, state.ownerToken);
      } catch (e) {
        setError("kitchen-error", e.message);
      }
    });
    row.appendChild(b);
  }
}

// ---------- Wiring ----------

$("btn-login").addEventListener("click", async () => {
  setError("login-error", null);
  $("btn-login").disabled = true;
  try {
    state.customerToken = await login($("email").value.trim(), $("password").value);
    show($("menu-card"));
    show($("order-card"));
    show($("stream-card"));
    show($("map-card"));
    show($("kitchen-card"));
    drawMap();
    $("order-meta").textContent = "Logged in. Pick a menu item, then Pay.";
    try {
      await loadRestaurants();
      await loadMenu();
    } catch (e) {
      setError("menu-error", e.message);
    }
    if (!state.ownerToken) {
      state.ownerToken = await login(OWNER_EMAIL, DEMO_PASSWORD);
    }
    buildKitchenButtons();
  } catch (e) {
    setError("login-error", e.message);
  } finally {
    $("btn-login").disabled = false;
  }
});

$("restaurant-select").addEventListener("change", (e) => {
  state.selectedRestaurantId = e.target.value;
  state.selectedItemId = null;
  loadMenu().catch((err) => setError("menu-error", err.message));
});

$("btn-refresh-menu").addEventListener("click", () => {
  setError("menu-error", null);
  loadMenu().catch((err) => setError("menu-error", err.message));
});

// Pay is intentionally NOT disabled while a request is in flight - the Day 4
// break depends on a real double-tap sending two overlapping POSTs.
$("btn-pay").addEventListener("click", () => {
  pay($("idem-mode").value);
});

$("btn-double-tap").addEventListener("click", () => {
  const mode = $("idem-mode").value;
  pay(mode);
  setTimeout(() => pay(mode), 80);
});

$("btn-new-key").addEventListener("click", () => {
  state.sharedIdemKey = newKey();
  $("order-meta").textContent = `Shared idempotency key rotated (...${state.sharedIdemKey.slice(-8)})`;
});

$("btn-track").addEventListener("click", () => {
  if (!state.orderId || !state.customerToken) return;
  setError("order-error", null);
  connectSse(state.orderId, state.customerToken).catch((e) => {
    if (e.name !== "AbortError") setError("order-error", e.message);
  });
});

renderTimeline();
