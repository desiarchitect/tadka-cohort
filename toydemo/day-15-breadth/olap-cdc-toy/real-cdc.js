#!/usr/bin/env node
/**
 * Real CDC relay — append order events from jsonl into Postgres rollup.
 *
 * Simulates: order service writes OLTP → CDC → analytics consumer updates rollup.
 *
 * Prerequisites: docker compose up -d postgres, tables exist (run real-db.js once)
 *
 * Usage: node real-cdc.js
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const EVENTS_FILE = path.join(__dirname, 'cdc', 'order-events.jsonl');
const BATCH = parseInt(process.env.CDC_BATCH || '5000', 10);
const DOCKER_PSQL = 'docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1';

function runPsql(sql) {
  return execSync(DOCKER_PSQL, {
    encoding: 'utf8',
    input: sql.trim() + '\n',
    stdio: ['pipe', 'pipe', 'pipe']
  });
}

function ensureEventsFile() {
  fs.mkdirSync(path.dirname(EVENTS_FILE), { recursive: true });
  if (fs.existsSync(EVENTS_FILE)) return;

  const regions = ['koramangala', 'indiranagar', 'hsr', 'whitefield'];
  const lines = [];
  const today = new Date().toISOString().slice(0, 10);
  for (let i = 0; i < BATCH; i++) {
    lines.push(JSON.stringify({
      orderId: `cdc-${i + 1}`,
      region: regions[i % regions.length],
      amount: Math.round((Math.random() * 900 + 100) * 100) / 100,
      day: today
    }));
  }
  fs.writeFileSync(EVENTS_FILE, lines.join('\n') + '\n');
  console.log(`Generated ${BATCH} CDC events → ${EVENTS_FILE}`);
}

function applyBatch(events) {
  const values = events.map(ev =>
    `('${ev.region}', '${ev.day}'::date, ${ev.amount}, 1)`
  ).join(',\n');
  runPsql(`
    INSERT INTO analytics_rollup (region, day, total_amount, order_count)
    VALUES ${values}
    ON CONFLICT (region, day)
    DO UPDATE SET
      total_amount = analytics_rollup.total_amount + EXCLUDED.total_amount,
      order_count = analytics_rollup.order_count + 1;
  `);
}

console.log('=== OLAP CDC Toy — Real CDC Relay ===\n');

ensureEventsFile();

const lines = fs.readFileSync(EVENTS_FILE, 'utf8').trim().split('\n');
const events = lines.map(l => JSON.parse(l));
const start = Date.now();
const CHUNK = 500;

for (let i = 0; i < events.length; i += CHUNK) {
  applyBatch(events.slice(i, i + CHUNK));
}

const applied = events.length;

const elapsed = Date.now() - start;
console.log(`Applied ${applied} CDC events to analytics_rollup in ${elapsed}ms`);
console.log('OLTP fact table was NOT scanned — this is the streaming maintenance path.');
console.log('\nNow run: node real-db.js --mode=fix  (rollup includes new events)');