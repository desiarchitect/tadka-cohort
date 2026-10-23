// Tadka: SMOKE test (Day 16)
//
// WHAT IT DOES: 1 virtual user for 30 seconds runs the shared customer session from lib.js about
// 15 times: list restaurants, open a menu, think 1 to 3 seconds, and (if ORDER_TOKEN and
// ORDER_CUSTOMER_ID are set) occasionally place an order.
// WHAT IT TESTS: that the whole path works at all (gateway routing, Restaurant service, Redis cache,
// auth, order write). It is NOT a load test. If it fails you have a bug or a missing setting, not a
// scaling problem. Run it first, every time, before any real load.
// WHAT YOU SEE: 30 of 30 checks pass, browse_errors 0%, rate_limited 0, thresholds green.
// orders_placed is often 0 because only about 2 of 15 sessions order.
//
//   k6 run k6/smoke.js
//   k6 run -e BASE_URL=http://localhost:8080 k6/smoke.js
//   k6 run -e ORDER_TOKEN=<jwt> -e ORDER_CUSTOMER_ID=<id> k6/smoke.js   # include the order write path

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
