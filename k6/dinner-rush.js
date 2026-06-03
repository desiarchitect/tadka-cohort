// Tadka — Dinner-Rush Load Profile (Week 2 Break Kit)
//
// This profile ramps *concurrency*, not just request rate. At 1 lakh/day the
// total volume is tiny — what breaks the monolith during the 7-10pm rush is
// many users hitting the hot read paths (restaurant list + menu) at once while
// a few unindexed queries hold connections open.
//
// Usage:
//   k6 run k6/dinner-rush.js
//   k6 run -e BASE_URL=http://localhost:5224 -e PEAK_VUS=200 k6/dinner-rush.js
//
// Read the before/after p99 and the pool-timeout error rate. That comparison —
// not the code — is the deliverable.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5224';
const PEAK_VUS = parseInt(__ENV.PEAK_VUS || '150', 10);

// Seeded restaurants (see docs/database-schema.md "Seed Data").
const RESTAURANT_IDS = [
  'a1b2c3d4-0001-4000-8000-000000000001', // Meghana Foods
  'a1b2c3d4-0002-4000-8000-000000000002', // Truffles
  'a1b2c3d4-0003-4000-8000-000000000003', // Vidyarthi Bhavan
];

const browseErrors = new Rate('browse_errors');
const menuLatency = new Trend('menu_latency', true);

export const options = {
  // A dinner rush: quiet → ramp up → sustained peak → wind down.
  stages: [
    { duration: '30s', target: Math.ceil(PEAK_VUS * 0.2) }, // 6:30pm, warming up
    { duration: '1m', target: PEAK_VUS },                    // 7:30pm, the rush
    { duration: '2m', target: PEAK_VUS },                    // 8:00-9:00pm, sustained peak
    { duration: '30s', target: 0 },                          // winding down
  ],
  thresholds: {
    // The NFR bar from Week 1. "Before" the fix this WILL fail; "after" it passes.
    'http_req_duration{name:menu}': ['p(99)<300'],
    'http_req_duration{name:list}': ['p(99)<300'],
    browse_errors: ['rate<0.01'], // < 1% errors (pool timeouts show up here)
  },
};

function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

export default function () {
  // 85% of dinner-rush traffic is browsing; ~15% actually place an order.
  const roll = Math.random();

  // 1) List restaurants (paginated) — every session starts here.
  const listRes = http.get(`${BASE_URL}/api/v1/restaurants?page=1&pageSize=10`, {
    tags: { name: 'list' },
  });
  browseErrors.add(listRes.status !== 200);
  check(listRes, { 'list 200': (r) => r.status === 200 });

  // 2) Open a restaurant's menu — the hot read the cohort caches on Day 6 (Redis).
  const restaurantId = pick(RESTAURANT_IDS);
  const menuRes = http.get(`${BASE_URL}/api/v1/restaurants/${restaurantId}/menu`, {
    tags: { name: 'menu' },
  });
  browseErrors.add(menuRes.status !== 200);
  menuLatency.add(menuRes.timings.duration);
  check(menuRes, { 'menu 200': (r) => r.status === 200 });

  // Think time — a real customer reads the menu before ordering.
  sleep(Math.random() * 2 + 1);

  if (roll < 0.15) {
    // 3) Place an order. Set ORDER_CUSTOMER_ID to a seeded customer to exercise
    //    the write path; otherwise this block is skipped (see Break Kit doc).
    const customerId = __ENV.ORDER_CUSTOMER_ID;
    if (customerId) {
      // Order against Meghana specifically — its menu item id below must belong to the
      // restaurant we order from (server-side pricing rejects an item not on the menu → 422).
      const payload = JSON.stringify({
        customerId,
        restaurantId: 'a1b2c3d4-0001-4000-8000-000000000001',
        items: [{ menuItemId: 'b1b2c3d4-0001-4000-8000-000000000001', quantity: 2 }],
        deliveryAddress: {
          line1: 'Flat 402, Green Apartments',
          line2: 'HSR Layout',
          city: 'Bangalore',
          pincode: '560102',
          latitude: 12.9141,
          longitude: 77.6411,
        },
      });
      const orderRes = http.post(`${BASE_URL}/api/v1/orders`, payload, {
        headers: { 'Content-Type': 'application/json' },
        tags: { name: 'order' },
      });
      check(orderRes, { 'order 201': (r) => r.status === 201 });
    }
  }
}
