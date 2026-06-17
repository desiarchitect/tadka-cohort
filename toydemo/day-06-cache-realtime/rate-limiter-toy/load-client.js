#!/usr/bin/env node
/**
 * Fires a boundary burst against the rate-limiter server.
 *
 * Usage:
 *   node load-client.js --algorithm=fixed-window
 *   node load-client.js --algorithm=token-bucket
 *
 * Server must be running: node server.js --algorithm=...
 */

const http = require('http');

const args = process.argv.slice(2);
const algorithm = (args.find(a => a.startsWith('--algorithm=')) || '--algorithm=fixed-window').split('=')[1];
const PORT = parseInt(process.env.PORT || '31091', 10);
const BURST_SIZE = 150;

function request() {
  return new Promise((resolve) => {
    const req = http.get(`http://127.0.0.1:${PORT}/`, (res) => {
      res.resume();
      resolve(res.statusCode);
    });
    req.on('error', () => resolve(0));
  });
}

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

async function main() {
  console.log(`=== Load client (${algorithm}) ===`);
  console.log(`Target: http://127.0.0.1:${PORT}/`);
  console.log('Strategy: wait for window boundary, fire 150 requests in a tight burst\n');

  // Align near a 1-second boundary
  const now = Date.now();
  const waitMs = 1000 - (now % 1000) + 950;
  console.log(`Waiting ${waitMs}ms to align burst near window boundary...`);
  await sleep(waitMs);

  const start = Date.now();
  const results = await Promise.all(Array(BURST_SIZE).fill(0).map(() => request()));
  const elapsed = Date.now() - start;

  const allowed = results.filter(s => s === 200).length;
  const rejected = results.filter(s => s === 429).length;
  const errors = results.filter(s => s !== 200 && s !== 429).length;

  console.log(`Burst completed in ${elapsed}ms`);
  console.log(`200 OK:    ${allowed}`);
  console.log(`429 limit: ${rejected}`);
  if (errors) console.log(`Errors:    ${errors}`);
  console.log(`\nFixed-window typically allows ~150-200 at boundary; token-bucket stays near 100-110.`);
}

main().catch(err => {
  console.error(err.message || err);
  process.exit(1);
});