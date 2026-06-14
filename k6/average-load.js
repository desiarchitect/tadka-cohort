// Tadka — AVERAGE LOAD test (Day 16)
//
// Ramp to ~50 concurrent users, hold, ramp down. This is a normal weekday-lunch
// crowd: steady, sustained, nothing dramatic. The question it answers is:
// "does the system meet its SLO under everyday traffic?" The Week-1 NFR bar
// (menu/list p99 < 300ms, <1% errors) SHOULD hold here — Day-14 indexes + the
// Redis cache earned it. Watch the Day-13 Grafana RED dashboard while it runs.
//
//   k6 run -e BASE_URL=http://localhost:8080 k6/average-load.js
//   # exercise the write path too:
//   k6 run -e ORDER_CUSTOMER_ID=<seeded-id> -e ORDER_TOKEN=<jwt> k6/average-load.js

import { userSession } from './lib.js';
import { sloThresholds } from './lib.js';

const PEAK_VUS = parseInt(__ENV.PEAK_VUS || '50', 10);

export const options = {
  stages: [
    { duration: '1m', target: PEAK_VUS }, // ramp to the lunch crowd
    { duration: '3m', target: PEAK_VUS }, // sustained — this is where the SLO is proven
    { duration: '1m', target: 0 },        // wind down
  ],
  thresholds: sloThresholds,
};

export default function () {
  userSession();
}
