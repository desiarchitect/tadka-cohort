// Tadka: EVOLUTION workload (Day 16 capstone, "1 user -> 1 million users")
//
// ONE workload that runs unchanged on every checkpoint branch (day-03 ... day-16), so the only
// variable between two runs is the architecture. It uses only routes that exist from Day 3 onward:
//   GET  /api/v1/restaurants?page=1&pageSize=10
//   GET  /api/v1/restaurants/{id}/menu
//   GET  /api/v1/orders?customerId=...&page=N     (order history)
//   POST /api/v1/orders                            (place an order)
// What changes across days is only the entry point (monolith :5224 -> gateway :8080 from Day 11)
// and auth (a Bearer token from Day 10). Both are passed in, never hard-coded.
//
// OPEN load model: a ramping-arrival-rate executor drives REQUESTS PER SECOND in steps, whatever the
// server's latency. (A closed VU model slows down when the server slows down, which hides the knee.)
// Each step is tagged, and handleSummary() reports, per step: p50/p95/p99, error rate and achieved RPS.
// CAPACITY = the highest step where p99 < SLO, errors < 1% and the server kept up (>= 95% of the target
// rate was actually served). That one number per checkpoint is what evolution/report.mjs compares.
//
//   k6 run -e SCENARIO=history -e LABEL=day-03-history k6/evolution.js
//   k6 run -e BASE_URL=http://localhost:8080 -e TOKEN=<jwt> -e SCENARIO=history -e LABEL=day-16-history k6/evolution.js
//
// Normally you don't call this by hand: evolution/run-checkpoint.ps1 / .sh starts the right services
// for a checkpoint, seeds the data, logs in, and runs it. See docs/runbooks/evolution-1-to-1m.md.
//
// SCENARIO (one request per iteration, so iteration rate == request rate):
//   browse  - 50/50 restaurant list / menu across the 3 seeded restaurants (the everyday read path)
//   history - order history for the seeded customer, random page 1..20 (needs the large seed, Day 5 win)
//   hotkey  - menu reads, HOT_SHARE (default 80%) to Meghana Foods (Day 6 Redis + single-flight win)
//   orders  - place an order (Day 7 async payment / Day 9 Kafka / Day 14 breaker wins)

import http from 'k6/http';
import exec from 'k6/execution';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5224';
const TOKEN = __ENV.TOKEN || '';
const SCENARIO = (__ENV.SCENARIO || 'browse').toLowerCase();
const LABEL = __ENV.LABEL || `${SCENARIO}-${Date.now()}`;
const OUT_DIR = __ENV.OUT_DIR || 'evolution/results';
const SLO_P99_MS = parseFloat(__ENV.SLO_P99_MS || '300');
const MAX_ERROR_RATE = parseFloat(__ENV.MAX_ERROR_RATE || '0.01');
const HOT_SHARE = parseFloat(__ENV.HOT_SHARE || '0.8');

// Requests-per-second steps. Default climbs to 400 RPS; raise it with RATES if a checkpoint never knees.
const RATES = (__ENV.RATES || '5,10,25,50,100,200,400').split(',').map((s) => parseInt(s.trim(), 10));
const STEP_SECONDS = parseInt(__ENV.STEP_SECONDS || '40', 10);
const RAMP_SECONDS = 3; // the short climb into each step is tagged "ramp" and excluded from the step's numbers
const WARMUP_SECONDS = parseInt(__ENV.WARMUP_SECONDS || '20', 10);
const WARMUP_RATE = 2;

// Seed data, identical since Day 1-3 (docs/database-schema.md "Seed Data").
const CUSTOMER_ID = __ENV.CUSTOMER_ID || 'c1b2c3d4-0001-4000-8000-000000000001'; // Priya
const MEGHANA_ID = 'a1b2c3d4-0001-4000-8000-000000000001';
const RESTAURANT_IDS = [
  MEGHANA_ID,
  'a1b2c3d4-0002-4000-8000-000000000002', // Truffles
  'a1b2c3d4-0003-4000-8000-000000000003', // Vidyarthi Bhavan
];
const MEGHANA_ITEM_ID = 'b1b2c3d4-0001-4000-8000-000000000001'; // Chicken Biryani (belongs to Meghana)

if (!['browse', 'history', 'hotkey', 'orders'].includes(SCENARIO)) {
  throw new Error(`SCENARIO must be browse|history|hotkey|orders, got "${SCENARIO}"`);
}

// Timeline: warm-up, then for each rate a short ramp and a hold.
const STAGES = [{ duration: `${WARMUP_SECONDS}s`, target: WARMUP_RATE }];
const WINDOWS = []; // [{ name, rate, start, end }] in seconds from test start, holds only
let t = WARMUP_SECONDS;
RATES.forEach((rate, i) => {
  STAGES.push({ duration: `${RAMP_SECONDS}s`, target: rate });
  STAGES.push({ duration: `${STEP_SECONDS - RAMP_SECONDS}s`, target: rate });
  WINDOWS.push({ name: `s${i}`, rate, start: t + RAMP_SECONDS, end: t + STEP_SECONDS });
  t += STEP_SECONDS;
});

// Per-step thresholds are never meant to fail (99999 ms): they exist because k6 only puts a tagged
// sub-metric into the end-of-test summary when a threshold references it.
const thresholds = {};
WINDOWS.forEach((w) => {
  thresholds[`http_req_duration{step:${w.name}}`] = ['p(99)<999999'];
  thresholds[`http_req_failed{step:${w.name}}`] = ['rate<=1'];
  thresholds[`http_reqs{step:${w.name}}`] = ['count>=0'];
});

export const options = {
  scenarios: {
    evolution: {
      executor: 'ramping-arrival-rate',
      startRate: WARMUP_RATE,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: parseInt(__ENV.MAX_VUS || '1500', 10),
      stages: STAGES,
    },
  },
  thresholds,
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

function stepTag() {
  const elapsed = exec.instance.currentTestRunDuration / 1000;
  for (const w of WINDOWS) {
    if (elapsed >= w.start && elapsed < w.end) return w.name;
  }
  return elapsed < WARMUP_SECONDS ? 'warmup' : 'ramp';
}

function authHeaders(extra) {
  const h = Object.assign({}, extra || {});
  if (TOKEN) h.Authorization = `Bearer ${TOKEN}`;
  return h;
}

function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

function uuid() {
  // RFC 4122 v4-shaped; good enough for an Idempotency-Key (Day 4+ dedups on it, Day 3 ignores it).
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

const params = (name, headers) => ({ headers, tags: { name, step: stepTag() }, timeout: '10s' });

export default function () {
  let res;
  if (SCENARIO === 'browse') {
    if (Math.random() < 0.5) {
      res = http.get(`${BASE_URL}/api/v1/restaurants?page=1&pageSize=10`, params('list', authHeaders()));
    } else {
      res = http.get(`${BASE_URL}/api/v1/restaurants/${pick(RESTAURANT_IDS)}/menu`, params('menu', authHeaders()));
    }
    check(res, { 'status 200': (r) => r.status === 200 });
  } else if (SCENARIO === 'hotkey') {
    const id = Math.random() < HOT_SHARE ? MEGHANA_ID : pick(RESTAURANT_IDS.slice(1));
    res = http.get(`${BASE_URL}/api/v1/restaurants/${id}/menu`, params(id === MEGHANA_ID ? 'hot_menu' : 'menu', authHeaders()));
    check(res, { 'status 200': (r) => r.status === 200 });
  } else if (SCENARIO === 'history') {
    // customerId is honoured up to Day 9; from Day 10 a customer token always sees only its own orders
    // (ADR-031), which is the same customer, so the query is identical on every checkpoint.
    const page = 1 + Math.floor(Math.random() * 20);
    res = http.get(`${BASE_URL}/api/v1/orders?customerId=${CUSTOMER_ID}&page=${page}&pageSize=10`, params('history', authHeaders()));
    check(res, { 'status 200': (r) => r.status === 200 });
  } else {
    const body = JSON.stringify({
      customerId: CUSTOMER_ID,
      restaurantId: MEGHANA_ID,
      items: [{ menuItemId: MEGHANA_ITEM_ID, quantity: 2 }],
      deliveryAddress: {
        line1: 'Flat 402, Green Apartments',
        line2: 'HSR Layout',
        city: 'Bangalore',
        pincode: '560102',
        latitude: 12.9141,
        longitude: 77.6411,
      },
    });
    res = http.post(`${BASE_URL}/api/v1/orders`, body,
      params('order', authHeaders({ 'Content-Type': 'application/json', 'Idempotency-Key': uuid() })));
    check(res, { 'status 201': (r) => r.status === 201 });
  }
}

// ---- summary: per-step table + capacity, written as JSON for evolution/report.mjs ------------------

function num(v) {
  return typeof v === 'number' && isFinite(v) ? Math.round(v * 100) / 100 : null;
}

export function handleSummary(data) {
  const m = data.metrics;
  const steps = WINDOWS.map((w) => {
    const dur = m[`http_req_duration{step:${w.name}}`];
    const failed = m[`http_req_failed{step:${w.name}}`];
    const reqs = m[`http_reqs{step:${w.name}}`];
    const count = reqs ? reqs.values.count : 0;
    const holdSeconds = w.end - w.start;
    const achieved = count / holdSeconds;
    const p99 = dur ? num(dur.values['p(99)']) : null;
    const errorRate = failed ? num(failed.values.rate) : null;
    const keptUp = achieved >= 0.95 * w.rate;
    const ok = count > 0 && p99 !== null && p99 < SLO_P99_MS && (errorRate || 0) < MAX_ERROR_RATE && keptUp;
    return {
      step: w.name,
      targetRps: w.rate,
      achievedRps: num(achieved),
      requests: count,
      p50: dur ? num(dur.values.med) : null,
      p95: dur ? num(dur.values['p(95)']) : null,
      p99,
      errorRate,
      keptUp,
      ok,
    };
  });

  // Capacity = the last step of the unbroken run of passing steps from the bottom. A step that passes
  // after a failed one is noise, not capacity.
  let capacityRps = 0;
  for (const s of steps) {
    if (!s.ok) break;
    capacityRps = s.targetRps;
  }
  const atCapacity = steps.find((s) => s.targetRps === capacityRps) || null;
  const firstFail = steps.find((s) => !s.ok) || null;

  const result = {
    label: LABEL,
    scenario: SCENARIO,
    baseUrl: BASE_URL,
    authenticated: Boolean(TOKEN),
    sloP99Ms: SLO_P99_MS,
    maxErrorRate: MAX_ERROR_RATE,
    stepSeconds: STEP_SECONDS,
    capacityRps,
    p99AtCapacity: atCapacity ? atCapacity.p99 : null,
    firstFailure: firstFail
      ? { targetRps: firstFail.targetRps, p99: firstFail.p99, errorRate: firstFail.errorRate, keptUp: firstFail.keptUp }
      : null,
    steps,
    totals: {
      requests: m.http_reqs ? m.http_reqs.values.count : 0,
      errorRate: m.http_req_failed ? num(m.http_req_failed.values.rate) : null,
      droppedIterations: m.dropped_iterations ? m.dropped_iterations.values.count : 0,
    },
    finishedAt: new Date().toISOString(),
  };

  const lines = [
    '',
    `Tadka evolution: ${LABEL}  (scenario=${SCENARIO}, base=${BASE_URL}, auth=${Boolean(TOKEN)})`,
    'step  target  achieved     p50      p99   errors  ok',
    ...steps.map((s) =>
      `${s.step.padEnd(5)} ${String(s.targetRps).padStart(6)}  ${String(s.achievedRps).padStart(8)} ` +
      `${String(s.p50).padStart(7)}  ${String(s.p99).padStart(7)}  ${String(((s.errorRate || 0) * 100).toFixed(2) + '%').padStart(7)}  ${s.ok ? 'yes' : 'NO'}`),
    `CAPACITY: ${capacityRps} RPS (p99 < ${SLO_P99_MS} ms, errors < ${MAX_ERROR_RATE * 100}%, kept up)`,
    `results: ${OUT_DIR}/${LABEL}.json`,
    '',
  ];

  const out = { stdout: lines.join('\n') };
  out[`${OUT_DIR}/${LABEL}.json`] = JSON.stringify(result, null, 2);
  return out;
}
