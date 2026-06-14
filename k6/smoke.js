// Tadka — SMOKE test (Day 16)
//
// 1 virtual user, 30 seconds. This is NOT a load test — it's a sanity check:
// "is the system functional at all?" If the smoke test fails, you have a BUG,
// not a scaling problem. Run it first, every time, before any real load.
//
//   k6 run k6/smoke.js
//   k6 run -e BASE_URL=http://localhost:8080 k6/smoke.js

import { userSession } from './lib.js';
import { sloThresholds } from './lib.js';

export const options = {
  vus: 1,
  duration: '30s',
  thresholds: sloThresholds,
};

export default function () {
  userSession();
}
