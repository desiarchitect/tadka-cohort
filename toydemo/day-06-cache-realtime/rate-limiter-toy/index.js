#!/usr/bin/env node
/**
 * Rate Limiter Algorithms Toy (failure-first demo)
 *
 * Scenario: protect the menu API at 100 requests per second.
 *
 * BREAK — fixed window counter:
 *   Resets the count at each window boundary. A burst straddling the boundary
 *   can pass 2× the limit (100 at end of window + 100 at start).
 *
 * FIX — token bucket:
 *   Tokens refill continuously. Bursts are absorbed up to bucket capacity,
 *   but sustained traffic cannot exceed the rate; no boundary double-dip.
 *
 * Real HTTP server (optional): node server.js --algorithm=fixed-window|token-bucket
 *   Then hammer with: node load-client.js
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const LIMIT_PER_SEC = 100;
const BUCKET_CAPACITY = 100;
const WINDOW_MS = 1000;

// 150 requests timed to exploit the fixed-window boundary:
// 75 arrive at t=950ms (window 0), 75 at t=1050ms (window 1)
const BURST_SCHEDULE = [
  ...Array(75).fill(950),
  ...Array(75).fill(1050)
];

class FixedWindowLimiter {
  constructor(limit, windowMs) {
    this.limit = limit;
    this.windowMs = windowMs;
    this.windowStart = 0;
    this.count = 0;
  }

  tryAcquire(nowMs) {
    if (nowMs - this.windowStart >= this.windowMs) {
      this.windowStart = nowMs;
      this.count = 0;
    }
    if (this.count < this.limit) {
      this.count += 1;
      return true;
    }
    return false;
  }
}

class TokenBucketLimiter {
  constructor(ratePerSec, capacity) {
    this.ratePerSec = ratePerSec;
    this.capacity = capacity;
    this.tokens = capacity;
    this.lastRefillMs = 0;
  }

  tryAcquire(nowMs) {
    if (this.lastRefillMs === 0) this.lastRefillMs = nowMs;

    const elapsedSec = (nowMs - this.lastRefillMs) / 1000;
    this.tokens = Math.min(this.capacity, this.tokens + elapsedSec * this.ratePerSec);
    this.lastRefillMs = nowMs;

    if (this.tokens >= 1) {
      this.tokens -= 1;
      return true;
    }
    return false;
  }
}

function runSimulation(limiter, label) {
  let allowed = 0;
  let rejected = 0;
  const allowedByWindow = {};

  for (const arrivalMs of BURST_SCHEDULE) {
    const windowKey = Math.floor(arrivalMs / WINDOW_MS);
    if (limiter.tryAcquire(arrivalMs)) {
      allowed += 1;
      allowedByWindow[windowKey] = (allowedByWindow[windowKey] || 0) + 1;
    } else {
      rejected += 1;
    }
  }

  return { label, allowed, rejected, allowedByWindow, limit: LIMIT_PER_SEC };
}

function printResult(result) {
  console.log(`Algorithm:     ${result.label}`);
  console.log(`Limit:         ${result.limit} req/s`);
  console.log(`Burst sent:    ${BURST_SCHEDULE.length} requests (75 @ t=950ms + 75 @ t=1050ms)`);
  console.log(`Allowed:       ${result.allowed}`);
  console.log(`Rejected:      ${result.rejected}`);
  console.log(`Per-window:    ${JSON.stringify(result.allowedByWindow)}`);
}

console.log('=== Rate Limiter Algorithms Toy ===');
console.log(`Protect an endpoint at ${LIMIT_PER_SEC} requests/second`);
console.log('Attack: boundary burst — 75 reqs just before reset + 75 just after\n');

console.log('>>> IMPORTANT: THIS IS A PURE-JS SIMULATION (deterministic arrival times).');
console.log('>>> It shows the fixed-window boundary burst flaw vs token-bucket smooth limiting.');
console.log('>>> For real HTTP 200/429 responses on localhost:');
console.log('>>>   1. node server.js --algorithm=fixed-window   (terminal 1)');
console.log('>>>   2. node load-client.js --algorithm=fixed-window   (terminal 2)');
console.log('>>>   3. Repeat with token-bucket. See RUN-AND-TEST.md.\n');

if (mode === 'break') {
  console.log('--- BREAK: Fixed window counter ---\n');
  printResult(runSimulation(
    new FixedWindowLimiter(LIMIT_PER_SEC, WINDOW_MS),
    'Fixed window (resets count every 1s)'
  ));
  console.log('\nWhy this breaks:');
  console.log('- Two adjacent windows each allow 100 → 200 requests in ~100ms.');
  console.log('- Attackers time bursts to window boundaries (classic interview trap).');
  console.log('- Per-instance counters make it worse behind a load balancer (N × limit).');

} else if (mode === 'fix') {
  console.log('--- FIX: Token bucket ---\n');
  printResult(runSimulation(
    new TokenBucketLimiter(LIMIT_PER_SEC, BUCKET_CAPACITY),
    'Token bucket (continuous refill, capacity=100)'
  ));
  console.log('\nWhy this works better:');
  console.log('- Tokens refill smoothly — no hard reset to exploit.');
  console.log('- Allows short bursts up to bucket capacity, then enforces steady rate.');
  console.log('- Pair with Redis (Day 6 Tadka gateway pattern) for distributed counts.');

} else {
  console.log('Usage:');
  console.log('  node index.js --mode=break');
  console.log('  node index.js --mode=fix');
  process.exit(1);
}

console.log('\nRun the other mode and compare allowed count at the boundary burst.');