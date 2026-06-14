// Tadka — shared k6 helpers (Day 16 load-test suite)
//
// One request flow, reused by every profile (smoke / average / stress / spike /
// dinner-rush) so the four profiles differ ONLY in their VU-vs-time shape — the
// thing each load-test *type* is meant to isolate. Don't duplicate the flow per
// file; change the shape in the profile, keep the work here.
//
// Default target is the GATEWAY (:8080) — the canonical "4 services + gateway"
// system. Browse reads (list + menu) route through YARP to the Restaurant
// service (anonymous, Redis-cached since Day 6/12); order writes route to the
// monolith and need a JWT (Day 10) + a seeded customer id.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

export const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';

// Seeded data (see docs/database-schema.md "Seed Data"). The order payload below
// must reference an item that belongs to the restaurant we order from, or
// server-side pricing rejects it with 422.
export const RESTAURANT_IDS = [
  'a1b2c3d4-0001-4000-8000-000000000001', // Meghana Foods
  'a1b2c3d4-0002-4000-8000-000000000002', // Truffles
  'a1b2c3d4-0003-4000-8000-000000000003', // Vidyarthi Bhavan
];
const MEGHANA_ID = 'a1b2c3d4-0001-4000-8000-000000000001';
const MEGHANA_ITEM_ID = 'b1b2c3d4-0001-4000-8000-000000000001';

// Order writes are opt-in: set ORDER_CUSTOMER_ID (a seeded customer) AND
// ORDER_TOKEN (a JWT from POST /api/v1/auth/login) to exercise the write path.
// Without them, a run still drives the hot read paths (the dinner-rush killer).
const ORDER_CUSTOMER_ID = __ENV.ORDER_CUSTOMER_ID;
const ORDER_TOKEN = __ENV.ORDER_TOKEN;
const ORDER_SHARE = parseFloat(__ENV.ORDER_SHARE || '0.15'); // 15% of sessions order

// Shared custom metrics — the numbers that ARE the deliverable.
export const browseErrors = new Rate('browse_errors');
export const menuLatency = new Trend('menu_latency', true);

// The NFR bar from Week 1 (ADR-014 indexing + ADR-018 cache make it hold).
// Re-used by every profile that wants to assert the SLO.
export const sloThresholds = {
  'http_req_duration{name:menu}': ['p(99)<300'],
  'http_req_duration{name:list}': ['p(99)<300'],
  browse_errors: ['rate<0.01'], // <1% — pool timeouts / 5xx surface here
};

function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

// One customer session: list restaurants → open a menu → (maybe) place an order.
export function userSession() {
  // 1) List restaurants (paginated) — every session starts here.
  const listRes = http.get(`${BASE_URL}/api/v1/restaurants?page=1&pageSize=10`, {
    tags: { name: 'list' },
  });
  browseErrors.add(listRes.status !== 200);
  check(listRes, { 'list 200': (r) => r.status === 200 });

  // 2) Open a restaurant's menu — the hot read (Redis cache-aside, Day 6).
  const restaurantId = pick(RESTAURANT_IDS);
  const menuRes = http.get(`${BASE_URL}/api/v1/restaurants/${restaurantId}/menu`, {
    tags: { name: 'menu' },
  });
  browseErrors.add(menuRes.status !== 200);
  menuLatency.add(menuRes.timings.duration);
  check(menuRes, { 'menu 200': (r) => r.status === 200 });

  // Think time — a real customer reads the menu before ordering.
  sleep(Math.random() * 2 + 1);

  // 3) ~15% place an order (opt-in via ORDER_CUSTOMER_ID + ORDER_TOKEN).
  if (Math.random() < ORDER_SHARE && ORDER_CUSTOMER_ID && ORDER_TOKEN) {
    const payload = JSON.stringify({
      customerId: ORDER_CUSTOMER_ID,
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
    const orderRes = http.post(`${BASE_URL}/api/v1/orders`, payload, {
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${ORDER_TOKEN}`,
      },
      tags: { name: 'order' },
    });
    check(orderRes, { 'order 201': (r) => r.status === 201 });
  }
}
