#!/usr/bin/env node
/**
 * HTTP server with selectable rate limiter for real 200/429 verification.
 *
 * Usage:
 *   node server.js --algorithm=fixed-window
 *   node server.js --algorithm=token-bucket
 */

const http = require('http');

const args = process.argv.slice(2);
const algorithm = (args.find(a => a.startsWith('--algorithm=')) || '--algorithm=fixed-window').split('=')[1];
const PORT = parseInt(process.env.PORT || '31091', 10);

const LIMIT_PER_SEC = 100;
const WINDOW_MS = 1000;
const BUCKET_CAPACITY = 100;

class FixedWindowLimiter {
  constructor(limit, windowMs) {
    this.limit = limit;
    this.windowMs = windowMs;
    this.windowStart = Date.now();
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
    this.lastRefillMs = Date.now();
  }

  tryAcquire(nowMs) {
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

const limiter = algorithm === 'token-bucket'
  ? new TokenBucketLimiter(LIMIT_PER_SEC, BUCKET_CAPACITY)
  : new FixedWindowLimiter(LIMIT_PER_SEC, WINDOW_MS);

const server = http.createServer((req, res) => {
  const now = Date.now();
  if (limiter.tryAcquire(now)) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true, algorithm, ts: now }));
  } else {
    res.writeHead(429, {
      'Content-Type': 'application/json',
      'Retry-After': '1'
    });
    res.end(JSON.stringify({ ok: false, error: 'rate_limited', algorithm }));
  }
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Rate limiter server listening on http://127.0.0.1:${PORT}`);
  console.log(`Algorithm: ${algorithm} (${LIMIT_PER_SEC} req/s)`);
  console.log('Run load-client.js in another terminal.');
});