#!/usr/bin/env node
/**
 * Hot Key / Cache Stampede Toy (failure-first demo)
 *
 * Scenario: popular restaurant menu key expires at dinner rush.
 * 200 concurrent requests all miss cache at the same instant.
 *
 * BREAK — naive cache-aside: every miss hits the DB.
 * FIX   — single-flight lock: one refresher, others wait + re-read cache (ADR-019).
 *
 * Real Redis: node real-redis.js --mode=break / --mode=fix
 *   (uses project's redis container — docker compose up -d redis)
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const CONCURRENT = 200;
const DB_LATENCY_MS = 50;
const LOCK_WAIT_MS = 20;
const CACHE_KEY = 'menu:restaurant:42';

let dbQueryCount = 0;
let cache = null;

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

async function fetchFromDb() {
  dbQueryCount += 1;
  await sleep(DB_LATENCY_MS);
  return { restaurantId: 42, items: 128, source: 'database' };
}

async function naiveGetOrSet() {
  if (cache) return cache;
  const value = await fetchFromDb();
  cache = value;
  return value;
}

// In-process single-flight (same algorithm as Tadka RedisCacheService / ADR-019)
let refreshInFlight = null;

async function singleFlightGetOrSet() {
  if (cache) return cache;

  if (!refreshInFlight) {
    refreshInFlight = (async () => {
      try {
        if (cache) return cache;
        const value = await fetchFromDb();
        cache = value;
        return value;
      } finally {
        refreshInFlight = null;
      }
    })();
    return refreshInFlight;
  }

  await sleep(LOCK_WAIT_MS);
  if (cache) return cache;
  return refreshInFlight;
}

async function runWorkload(getter, label) {
  dbQueryCount = 0;
  cache = null;

  const start = Date.now();
  const latencies = [];

  await Promise.all(
    Array.from({ length: CONCURRENT }, async () => {
      const t0 = Date.now();
      await getter();
      latencies.push(Date.now() - t0);
    })
  );

  latencies.sort((a, b) => a - b);
  const p50 = latencies[Math.floor(latencies.length * 0.5)];
  const p99 = latencies[Math.floor(latencies.length * 0.99)];

  return {
    label,
    concurrent: CONCURRENT,
    dbQueries: dbQueryCount,
    wallMs: Date.now() - start,
    p50Ms: p50,
    p99Ms: p99,
    aggregateDbWorkMs: dbQueryCount * DB_LATENCY_MS
  };
}

function printResult(r) {
  console.log(`Approach:          ${r.label}`);
  console.log(`Concurrent reqs:   ${r.concurrent} (all miss hot key ${CACHE_KEY})`);
  console.log(`DB queries issued: ${r.dbQueries}`);
  console.log(`Aggregate DB work: ${r.aggregateDbWorkMs}ms (${r.dbQueries} × ${DB_LATENCY_MS}ms)`);
  console.log(`Wall time:         ${r.wallMs}ms`);
  console.log(`Request p50:       ${r.p50Ms}ms`);
  console.log(`Request p99:       ${r.p99Ms}ms`);
}

console.log('=== Hot Key / Cache Stampede Toy ===');
console.log(`Hot key expires → ${CONCURRENT} simultaneous cache misses`);
console.log(`Simulated DB refresh cost: ${DB_LATENCY_MS}ms per query\n`);

console.log('>>> IMPORTANT: THIS IS A PURE-JS SIMULATION (in-memory cache + mutex).');
console.log('>>> Headline metric: how many DB queries fire on one expiry boundary.');
console.log('>>> For real Redis SET NX EX single-flight (same container as Tadka Day 6):');
console.log('>>>   1. docker compose up -d redis');
console.log('>>>   2. npm install');
console.log('>>>   3. node real-redis.js --mode=break   (then --mode=fix)');
console.log('>>> See RUN-AND-TEST.md.\n');

(async () => {
  if (mode === 'break') {
    console.log('--- BREAK: Naive cache-aside (no stampede protection) ---\n');
    printResult(await runWorkload(naiveGetOrSet, 'Naive cache-aside — every miss hits DB'));
    console.log('\nWhy this breaks:');
    console.log(`- TTL expires → ${CONCURRENT} requests all miss → ${CONCURRENT} DB refreshes.`);
    console.log('- Aggregate DB load spikes higher than having no cache at all.');
    console.log('- Connection pool drains; p99 explodes at every expiry boundary.');
    console.log('- This is the dinner-rush wound ADR-019 fixes in Tadka.');

  } else if (mode === 'fix') {
    console.log('--- FIX: Single-flight refresh (ADR-019 pattern) ---\n');
    printResult(await runWorkload(singleFlightGetOrSet, 'Single-flight lock — one refresher'));
    console.log('\nWhy this works:');
    console.log('- Exactly one caller refreshes; others wait briefly and re-read cache.');
    console.log('- DB sees ~1 query per expiry, not hundreds.');
    console.log('- In Tadka: Redis SET lock:{key} NX EX + guarded release.');

  } else {
    console.log('Usage:');
    console.log('  node index.js --mode=break');
    console.log('  node index.js --mode=fix');
    process.exit(1);
  }

  console.log('\nCompare DB query count — that is the stampede vs single-flight story.');
})().catch(err => {
  console.error(err);
  process.exit(1);
});