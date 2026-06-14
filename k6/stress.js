// Tadka — STRESS test (Day 16) — THE HEADLINE DEMO
//
// Keep climbing the concurrency until something breaks. Every system has a
// breaking point; the only question is whether you know yours BEFORE the IPL
// final does. We deliberately relax the latency threshold here — we are not
// asserting the SLO, we are HUNTING for the knee: the VU count where p99
// explodes, errors appear, and one resource saturates first.
//
// Read it live on the Day-13 dashboards:
//   - Grafana RED dashboard: where does p99 knee up? where do 5xx start?
//   - Which saturates first? DB connection pool (Npgsql) / Kafka consumer lag /
//     container memory. THAT first-to-fall resource is your real bottleneck.
//   - Jaeger: open a slow trace at the knee — which span owns the latency?
//
// The captured breaking-point number (e.g. "p99 crossed 1s at ~N VUs; the
// primary connection pool drained first") is the deliverable — not the script.
//
//   k6 run -e BASE_URL=http://localhost:8080 k6/stress.js
//   k6 run -e MAX_VUS=400 k6/stress.js

import { userSession } from './lib.js';

const MAX_VUS = parseInt(__ENV.MAX_VUS || '300', 10);

export const options = {
  stages: [
    { duration: '1m', target: Math.ceil(MAX_VUS * 0.17) }, // normal
    { duration: '2m', target: Math.ceil(MAX_VUS * 0.33) }, // above normal
    { duration: '2m', target: Math.ceil(MAX_VUS * 0.67) }, // stress
    { duration: '2m', target: MAX_VUS },                   // breaking point?
    { duration: '1m', target: 0 },                         // recovery
  ],
  thresholds: {
    // Relaxed on purpose: a breached threshold here is the FINDING, not a fail.
    // We still flag the catastrophic end of the scale so the run is readable.
    'http_req_duration{name:menu}': ['p(99)<5000'],
    browse_errors: ['rate<0.25'],
  },
};

export default function () {
  userSession();
}
