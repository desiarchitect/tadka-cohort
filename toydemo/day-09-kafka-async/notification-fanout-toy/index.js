#!/usr/bin/env node
/**
 * Notification / Promotional Fan-Out Toy (failure-first demo)
 *
 * Scenario: "50% off biryani" push to 10,000 users.
 *
 * BREAK — synchronous in-request loop: admin API blocks, client times out,
 *         most users never notified, retries duplicate sends.
 *
 * FIX   — one Kafka event + worker fan-out + inbox idempotency + DLQ for poison tokens.
 *
 * Real Kafka: node real-kafka.js --mode=fix (docker compose up -d kafka)
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const USERS = parseInt(process.env.USERS || '10000', 10);
const PUSH_LATENCY_MS = 2;
const CLIENT_TIMEOUT_MS = 5_000;
const WORKER_CONCURRENCY = 50;
const INVALID_TOKEN_RATE = 0.02; // 2% permanent failures → DLQ
const REDELIVERY_RATE = 0.1;   // 10% Kafka redeliveries on fix path

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

function buildUserList() {
  return Array.from({ length: USERS }, (_, i) => ({
    id: `user-${i + 1}`,
    tokenValid: (i + 1) % Math.round(1 / INVALID_TOKEN_RATE) !== 0
  }));
}

async function simulateSyncFanout(users) {
  const start = Date.now();
  const maxInWindow = Math.floor(CLIENT_TIMEOUT_MS / PUSH_LATENCY_MS);
  const sent = Math.min(users.length, maxInWindow);
  const clientTimedOut = sent < users.length;
  let duplicatesOnRetry = 0;

  // Brief pause so the demo feels "in-flight" without sleeping 2500×2ms
  await sleep(100);

  if (clientTimedOut && sent > 0) {
    duplicatesOnRetry = sent;
  }

  return {
    label: 'Synchronous in-request fan-out (naive admin API)',
    usersTargeted: users.length,
    pushesSent: sent,
    usersMissed: users.length - sent,
    clientTimedOut,
    duplicatePushesOnRetry: duplicatesOnRetry,
    wallMs: clientTimedOut ? CLIENT_TIMEOUT_MS : sent * PUSH_LATENCY_MS,
    simulatedWallMs: true,
    dlqCount: 0,
    inboxDeduped: 0
  };
}

async function simulateAsyncFanout(users) {
  const inbox = new Set();
  const dlq = [];
  let redeliveryAttempts = 0;
  let inboxDeduped = 0;
  const start = Date.now();

  // One event enqueued (Outbox → Kafka pattern)
  await sleep(1);

  async function deliverOne(user, attempt = 1) {
    if (!user.tokenValid) {
      if (attempt >= 3) {
        dlq.push(user.id);
        return { status: 'dlq' };
      }
      throw new Error('invalid_token');
    }
    const key = `promo-biryani-50:${user.id}`;
    if (inbox.has(key)) {
      inboxDeduped += 1;
      return { status: 'deduped' };
    }
    if (PUSH_LATENCY_MS > 0) await sleep(PUSH_LATENCY_MS);
    inbox.add(key);
    return { status: 'sent' };
  }

  const queue = [...users];
  const workers = Array.from({ length: WORKER_CONCURRENCY }, async () => {
    while (queue.length) {
      const user = queue.shift();
      if (!user) break;

      let attempt = 1;
      while (attempt <= 3) {
        try {
          const result = await deliverOne(user, attempt);
          if (result.status === 'dlq') break;
          break;
        } catch {
          attempt += 1;
          await sleep(5);
        }
      }

      // Simulate Kafka at-least-once redelivery
      if (Math.random() < REDELIVERY_RATE) {
        redeliveryAttempts += 1;
        try {
          await deliverOne(user, 1);
        } catch {
          // invalid token on redelivery — already in DLQ or will be
        }
      }
    }
  });

  await Promise.all(workers);

  return {
    label: 'Async Kafka fan-out + inbox idempotency + DLQ',
    usersTargeted: users.length,
    pushesSent: inbox.size,
    usersMissed: users.length - inbox.size - dlq.length,
    clientTimedOut: false,
    duplicatePushesOnRetry: 0,
    wallMs: Date.now() - start,
    dlqCount: dlq.length,
    inboxDeduped,
    redeliveryAttempts
  };
}

function printResult(r) {
  console.log(`Approach:                ${r.label}`);
  console.log(`Users targeted:          ${r.usersTargeted.toLocaleString()}`);
  console.log(`Pushes delivered:        ${r.pushesSent.toLocaleString()}`);
  console.log(`Users missed:            ${r.usersMissed.toLocaleString()}`);
  console.log(`Client timed out:        ${r.clientTimedOut ? 'YES' : 'no'}`);
  console.log(`Duplicate pushes (retry):${r.duplicatePushesOnRetry.toLocaleString()}`);
  console.log(`DLQ (poison tokens):     ${r.dlqCount.toLocaleString()}`);
  if (r.inboxDeduped != null) console.log(`Inbox deduped (redelivery): ${r.inboxDeduped.toLocaleString()}`);
  if (r.redeliveryAttempts != null) console.log(`Simulated redeliveries:  ${r.redeliveryAttempts.toLocaleString()}`);
  const wallNote = r.simulatedWallMs ? ' (simulated — sync path would block until timeout)' : '';
  console.log(`Wall time:               ${r.wallMs}ms${wallNote}`);
}

console.log('=== Notification / Promotional Fan-Out Toy ===');
console.log(`Campaign: push "${USERS.toLocaleString()} users" — 50% off biryani promo\n`);

console.log('>>> IMPORTANT: THIS IS A PURE-JS SIMULATION.');
console.log('>>> Transactional (order-confirmed) uses the same inbox pattern as Tadka Day 9.');
console.log('>>> For real Kafka produce/consume:');
console.log('>>>   1. docker compose up -d kafka');
console.log('>>>   2. npm install');
console.log('>>>   3. node real-kafka.js --mode=fix');
console.log('>>> See RUN-AND-TEST.md.\n');

(async () => {
  const users = buildUserList();

  if (mode === 'break') {
    console.log('--- BREAK: Sync fan-out in the admin HTTP request ---\n');
    printResult(await simulateSyncFanout(users));
    console.log('\nWhy this breaks:');
    console.log('- Request blocks for N × push_latency — gateway/client times out at 5s.');
    console.log(`- Only ~${CLIENT_TIMEOUT_MS / PUSH_LATENCY_MS} pushes fit in the timeout window.`);
    console.log('- Ops retries → duplicate notifications to users already pinged.');
    console.log('- Mass promo must never be on the critical HTTP path.');

  } else if (mode === 'fix') {
    console.log('--- FIX: One Kafka event + worker fan-out + inbox + DLQ ---\n');
    printResult(await simulateAsyncFanout(users));
    console.log('\nWhy this works:');
    console.log('- Admin API publishes one durable event and returns 202 in ms.');
    console.log('- Workers fan out at controlled concurrency.');
    console.log('- Inbox/dedupe key = exactly-once *effect* under at-least-once delivery (ADR-028).');
    console.log('- Permanent failures (invalid token) → DLQ after retries, not infinite loop.');

  } else {
    console.log('Usage:');
    console.log('  node index.js --mode=break');
    console.log('  node index.js --mode=fix');
    process.exit(1);
  }
})().catch(err => {
  console.error(err);
  process.exit(1);
});