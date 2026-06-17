#!/usr/bin/env node
/**
 * Real Redis verification for the Hot Key / Cache Stampede Toy.
 *
 * Uses the project's Redis (docker service: tadka-redis on localhost:6379).
 *
 * Prerequisites:
 *   docker compose up -d redis
 *   npm install
 *
 * Usage:
 *   node real-redis.js --mode=break
 *   node real-redis.js --mode=fix
 */

const Redis = require('ioredis');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const CONCURRENT = parseInt(process.env.CONCURRENT || '200', 10);
const DB_LATENCY_MS = 50;
const LOCK_TTL_SEC = 5;
const LOCK_WAIT_MS = 25;
const LOCK_RETRY = 4;

const CACHE_KEY = 'toydemo:stampede:menu:42';
const LOCK_KEY = 'toydemo:stampede:lock:menu:42';
const DB_COUNTER_KEY = 'toydemo:stampede:db-queries';

const redis = new Redis({
  host: process.env.REDIS_HOST || '127.0.0.1',
  port: parseInt(process.env.REDIS_PORT || '6379', 10),
  maxRetriesPerRequest: 1,
  connectTimeout: 3000
});

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

async function simulateDb() {
  await redis.incr(DB_COUNTER_KEY);
  await sleep(DB_LATENCY_MS);
  return JSON.stringify({ restaurantId: 42, items: 128, source: 'database' });
}

async function naiveGetOrSet() {
  const cached = await redis.get(CACHE_KEY);
  if (cached) return cached;

  const value = await simulateDb();
  await redis.set(CACHE_KEY, value, 'EX', 60);
  return value;
}

async function singleFlightGetOrSet() {
  const cached = await redis.get(CACHE_KEY);
  if (cached) return cached;

  const token = `${process.pid}-${Date.now()}-${Math.random()}`;
  const acquired = await redis.set(LOCK_KEY, token, 'NX', 'EX', LOCK_TTL_SEC);

  if (acquired === 'OK') {
    try {
      const again = await redis.get(CACHE_KEY);
      if (again) return again;

      const value = await simulateDb();
      await redis.set(CACHE_KEY, value, 'EX', 60);
      return value;
    } finally {
      const current = await redis.get(LOCK_KEY);
      if (current === token) {
        await redis.del(LOCK_KEY);
      }
    }
  }

  for (let i = 0; i < LOCK_RETRY; i++) {
    await sleep(LOCK_WAIT_MS);
    const hit = await redis.get(CACHE_KEY);
    if (hit) return hit;
  }

  return naiveGetOrSet();
}

async function resetDemoKeys() {
  await redis.del(CACHE_KEY, LOCK_KEY, DB_COUNTER_KEY);
}

async function runWorkload(getter, label) {
  await resetDemoKeys();

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
  const dbQueries = parseInt(await redis.get(DB_COUNTER_KEY) || '0', 10);

  return {
    label,
    concurrent: CONCURRENT,
    dbQueries,
    wallMs: Date.now() - start,
    p50Ms: latencies[Math.floor(latencies.length * 0.5)],
    p99Ms: latencies[Math.floor(latencies.length * 0.99)],
    aggregateDbWorkMs: dbQueries * DB_LATENCY_MS
  };
}

function printResult(r) {
  console.log(`Approach:          ${r.label}`);
  console.log(`Concurrent reqs:   ${r.concurrent}`);
  console.log(`DB queries issued: ${r.dbQueries} (redis key ${DB_COUNTER_KEY})`);
  console.log(`Aggregate DB work: ${r.aggregateDbWorkMs}ms`);
  console.log(`Wall time:         ${r.wallMs}ms`);
  console.log(`Request p50:       ${r.p50Ms}ms`);
  console.log(`Request p99:       ${r.p99Ms}ms`);
}

async function main() {
  console.log('=== Hot Key Stampede Toy — Real Redis Mode ===\n');
  console.log('Using project Redis (docker service: tadka-redis → localhost:6379).');
  console.log('Make sure it is running:  docker compose up -d redis\n');

  await redis.ping();

  if (mode === 'break') {
    console.log('--- BREAK: Naive cache-aside (no single-flight) ---\n');
    printResult(await runWorkload(naiveGetOrSet, 'Naive — parallel misses stampede DB'));
  } else if (mode === 'fix') {
    console.log('--- FIX: Redis SET NX EX single-flight (ADR-019) ---\n');
    printResult(await runWorkload(singleFlightGetOrSet, 'Single-flight lock'));
  } else {
    console.log('Usage:');
    console.log('  node real-redis.js --mode=break');
    console.log('  node real-redis.js --mode=fix');
    process.exit(1);
  }

  await resetDemoKeys();
  redis.disconnect();
  console.log('\nDB query count is the headline — compare break (~200) vs fix (~1-5).');
}

main().catch(err => {
  console.error('Error:', err.message || err);
  console.error('Is Redis up?  docker compose up -d redis');
  process.exit(1);
});