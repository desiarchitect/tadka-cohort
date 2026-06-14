// Tadka — SPIKE test (Day 16)
//
// Traffic doesn't always ramp politely. The IPL final ends, a celebrity tweets,
// Diwali hits 8pm — and load goes from a trickle to a flood in seconds, then
// drops just as fast. This profile: quiet → INSTANT spike → hold → drop →
// recovery window. Two things to watch, both on the Day-13/14 dashboards:
//
//   1) SURVIVAL: does the Day-14 circuit breaker trip on Payment→gateway and
//      fail fast (ms) instead of hanging? (Grafana panel
//      `tadka.payment.circuit_transitions{state}` — does it go OPEN?)
//   2) RECOVERY: after the spike drops, does p99 come back to baseline? If it
//      stays degraded, you have a leak — connection pool not released, Kafka
//      consumer lag that never drains, Redis memory not freed, threads stuck in
//      retry. Recovery is as important as survival.
//
//   k6 run -e BASE_URL=http://localhost:8080 k6/spike.js
//   k6 run -e SPIKE_VUS=300 k6/spike.js

import { userSession } from './lib.js';

const SPIKE_VUS = parseInt(__ENV.SPIKE_VUS || '200', 10);
const BASE_VUS = Math.max(5, Math.ceil(SPIKE_VUS * 0.05));

export const options = {
  stages: [
    { duration: '1m', target: BASE_VUS },   // calm before the storm
    { duration: '10s', target: SPIKE_VUS },  // SPIKE — the tweet lands
    { duration: '1m', target: SPIKE_VUS },   // sustained flood
    { duration: '10s', target: BASE_VUS },   // traffic falls off a cliff
    { duration: '2m', target: BASE_VUS },    // recovery window — watch p99 return
  ],
  thresholds: {
    // No hard SLO during a spike; we read survival + recovery on the dashboards.
    'http_req_duration{name:menu}': ['p(99)<5000'],
  },
};

export default function () {
  userSession();
}
