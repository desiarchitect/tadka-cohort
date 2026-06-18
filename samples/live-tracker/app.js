// Tadka live order tracker — vanilla JS, no build step.
// Uses fetch() for SSE so we can send Authorization (EventSource cannot).

const API = window.location.origin;

const MEGHANA_ID = "a1b2c3d4-0001-4000-8000-000000000001";
const MEGHANA_ITEM_ID = "b1b2c3d4-0001-4000-8000-000000000001";
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

// Meghana Foods (seeded restaurant) — approximate Indiranagar coords
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
};

const $ = (id) => document.getElementById(id);

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

async function placeOrder(token) {
  const payload = {
    restaurantId: MEGHANA_ID,
    items: [{ menuItemId: MEGHANA_ITEM_ID, quantity: 2 }],
    deliveryAddress: {
      line1: "Flat 402, Green Apartments",
      line2: "HSR Layout",
      city: "Bangalore",
      pincode: "560102",
      latitude: 12.9141,
      longitude: 77.6411,
    },
  };
  const res = await fetch(`${API}/api/v1/orders`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
      "Idempotency-Key": `live-tracker-${Date.now()}`,
    },
    body: JSON.stringify(payload),
  });
  if (!res.ok && res.status !== 200) {
    const body = await res.text();
    throw new Error(`Order failed (${res.status}): ${body}`);
  }
  const data = await res.json();
  return data.id;
}

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
  const seen = new Set(state.events.map((e) => e.status));

  for (const step of FLOW) {
    const hit = state.events.filter((e) => e.status === step);
    const li = document.createElement("li");
    if (hit.length) li.classList.add("active");
    const last = hit[hit.length - 1];
    li.innerHTML = `
      <span class="dot"></span>
      <div>
        <div><strong>${step}</strong></div>
        ${last ? `<div class="time">${last.message} · ${formatTime(last.timestamp)}</div>` : `<div class="time">waiting…</div>`}
      </div>`;
    ul.appendChild(li);
  }

  if (!seen.size && state.events.length) {
    // Non-standard status — append raw events at bottom
    for (const e of state.events) {
      const li = document.createElement("li");
      li.classList.add("active");
      li.innerHTML = `<span class="dot"></span><div><strong>${e.status}</strong><div class="time">${e.message}</div></div>`;
      ul.appendChild(li);
    }
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
      $("map-meta").textContent = "Delivery service may be down (Days 6–7) or rider not assigned yet.";
      return;
    }
    state.trackMeta = {
      riderName: data.agentName || "Rider",
      status: data.status,
    };
    const loc = data.location;
    if (loc?.latitude != null && loc?.longitude != null) {
      state.riderPos = { lat: loc.latitude, lng: loc.longitude };
      setMapPill(`${state.trackMeta.riderName} · ${data.status}`, true);
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

$("btn-login").addEventListener("click", async () => {
  setError("login-error", null);
  $("btn-login").disabled = true;
  try {
    state.customerToken = await login($("email").value.trim(), $("password").value);
    show($("order-card"));
    show($("stream-card"));
    show($("map-card"));
    show($("kitchen-card"));
    drawMap();
    $("order-meta").textContent = "Logged in. Place an order to start tracking.";
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

$("btn-place-order").addEventListener("click", async () => {
  setError("order-error", null);
  $("btn-place-order").disabled = true;
  try {
    state.orderId = await placeOrder(state.customerToken);
    $("order-meta").textContent = `Order ${state.orderId}`;
    $("btn-track").disabled = false;
    connectSse(state.orderId, state.customerToken).catch((e) => {
      if (e.name !== "AbortError") setError("order-error", e.message);
    });
  } catch (e) {
    setError("order-error", e.message);
  } finally {
    $("btn-place-order").disabled = false;
  }
});

$("btn-track").addEventListener("click", () => {
  if (!state.orderId || !state.customerToken) return;
  setError("order-error", null);
  connectSse(state.orderId, state.customerToken).catch((e) => {
    if (e.name !== "AbortError") setError("order-error", e.message);
  });
});

renderTimeline();