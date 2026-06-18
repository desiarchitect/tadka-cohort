// Tadka — HOT-KEY load profile (Day 6 / Day 14 stampede demo)
//
// ~80% of menu traffic hits ONE restaurant (Meghana Foods — the "celebrity"
// flash-sale pattern). Use this AFTER invalidating that menu's Redis key so
// every request misses at once, then compare with single-flight lock ON.
//
// Pair with:
//   - toydemo/day-06-cache-realtime/hot-key-stampede-toy (200 DB queries → 1)
//   - Grafana menu p99 + replica CPU during the run
//   - Day-14 Scenario 4 (Redis-down + hot-key stampede)
//
//   docker exec tadka-redis redis-cli DEL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
//   k6 run k6/hot-key.js
//   k6 run -e HOT_SHARE=0.95 -e PEAK_VUS=100 k6/hot-key.js

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';
import { BASE_URL, browseErrors, menuLatency } from './lib.js';

const MEGHANA_ID = 'a1b2c3d4-0001-4000-8000-000000000001';
const OTHER_IDS = [
  'a1b2c3d4-0002-4000-8000-000000000002', // Truffles
  'a1b2c3d4-0003-4000-8000-000000000003', // Vidyarthi Bhavan
];

const HOT_SHARE = parseFloat(__ENV.HOT_SHARE || '0.80');
const PEAK_VUS = parseInt(__ENV.PEAK_VUS || '80', 10);

export const meghanaHits = new Counter('meghana_menu_hits');
export const otherHits = new Counter('other_menu_hits');
export const hotKeyErrors = new Rate('hotkey_errors');
export const hotKeyMenuLatency = new Trend('hotkey_menu_latency', true);

function pickRestaurantId() {
  return Math.random() < HOT_SHARE ? MEGHANA_ID : OTHER_IDS[Math.floor(Math.random() * OTHER_IDS.length)];
}

export const options = {
  stages: [
    { duration: '30s', target: Math.ceil(PEAK_VUS * 0.25) },
    { duration: '1m', target: PEAK_VUS },
    { duration: '2m', target: PEAK_VUS },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    hotkey_errors: ['rate<0.15'],
    'http_req_duration{name:hot_menu}': ['p(99)<5000'],
  },
};

export default function () {
  const restaurantId = pickRestaurantId();
  const isHot = restaurantId === MEGHANA_ID;

  const menuRes = http.get(`${BASE_URL}/api/v1/restaurants/${restaurantId}/menu`, {
    tags: { name: isHot ? 'hot_menu' : 'cold_menu' },
  });

  const ok = menuRes.status === 200;
  hotKeyErrors.add(!ok);
  browseErrors.add(!ok);
  hotKeyMenuLatency.add(menuRes.timings.duration);
  menuLatency.add(menuRes.timings.duration);

  if (isHot) meghanaHits.add(1);
  else otherHits.add(1);

  check(menuRes, { 'menu 200': (r) => r.status === 200 });

  // Short think time — this simulates a feed refresh storm, not leisurely browsing.
  sleep(Math.random() * 0.5 + 0.1);
}