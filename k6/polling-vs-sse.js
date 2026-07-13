// Tadka — Polling vs SSE (Day 6 Break Kit)
//
// Same question, two ways to ask it repeatedly: "has this order's status changed?" Polling
// re-asks on a timer, one full HTTP request each time. SSE asks ONCE and keeps the connection
// open — the server pushes only when something actually changes.
//
// k6's stock http module has no native SSE/streaming primitive, so this models the comparison
// honestly rather than faking it: the polling scenario loops a GET once per second per VU for
// the run; the SSE scenario makes ONE GET per VU to the streaming endpoint with a timeout equal
// to the run duration — since the server never closes an SSE stream on its own, that single
// request stays open for the whole run, so the REQUEST COUNT each scenario produces is the real,
// comparable number: polling scales with (VUs x duration), SSE scales with VUs alone.
//
// Usage:
//   k6 run k6/polling-vs-sse.js                              # both scenarios, one run
//   k6 run -e SCENARIO=polling -e VUS=200 k6/polling-vs-sse.js
//   k6 run -e SCENARIO=sse     -e VUS=200 k6/polling-vs-sse.js
//
// Read total requests + browser QPS on the Grafana RED dashboard during each run — that
// comparison, not this script, is the deliverable.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5224';
const VUS = parseInt(__ENV.VUS || '50', 10);
const DURATION_SECONDS = parseInt(__ENV.DURATION_SECONDS || '30', 10);
const SCENARIO = __ENV.SCENARIO || 'both'; // 'polling' | 'sse' | 'both'
const ORDER_ID = __ENV.ORDER_ID; // a real seeded/placed order id — required

const requestsIssued = new Counter('requests_issued');

if (!ORDER_ID) {
  throw new Error('Set -e ORDER_ID=<a real order id> — see docs/runbooks/day-06.md.');
}

const scenarios = {};

if (SCENARIO === 'polling' || SCENARIO === 'both') {
  scenarios.polling = {
    executor: 'constant-vus',
    vus: VUS,
    duration: `${DURATION_SECONDS}s`,
    exec: 'poll',
  };
}

if (SCENARIO === 'sse' || SCENARIO === 'both') {
  scenarios.sse = {
    executor: 'per-vu-iterations',
    vus: VUS,
    iterations: 1,
    maxDuration: `${DURATION_SECONDS + 10}s`,
    exec: 'stream',
    startTime: SCENARIO === 'both' ? `${DURATION_SECONDS + 5}s` : '0s', // run sequentially, not overlapped, for a clean comparison
  };
}

export const options = { scenarios };

// Polling: one GET per second per VU, for the whole run — this is what "refresh the order
// screen every second" actually costs the server. The sleep(1) is load-bearing: without it this
// is a tight loop, not a 1-second poll, and at any real VU count it blows straight through the
// rate limiter (ADR-049) before it ever demonstrates the polling-cost lesson.
export function poll() {
  const res = http.get(`${BASE_URL}/api/v1/orders/${ORDER_ID}`, { tags: { name: 'poll' } });
  requestsIssued.add(1);
  check(res, { 'poll 200': (r) => r.status === 200 });
  sleep(1);
}

// SSE: ONE request that stays open for the run duration. The server never closes an SSE stream
// on its own, so this blocks until the timeout — exactly modeling "one persistent connection."
export function stream() {
  const res = http.get(`${BASE_URL}/api/v1/orders/${ORDER_ID}/events`, {
    tags: { name: 'sse' },
    timeout: `${DURATION_SECONDS}s`,
  });
  requestsIssued.add(1);
  // A timeout abort is the EXPECTED way this ends (the connection was still open, doing its
  // job) — status may be 0 in that case, which is success here, not a check failure.
  check(res, { 'sse opened or ran to timeout': (r) => r.status === 200 || r.status === 0 });
}
