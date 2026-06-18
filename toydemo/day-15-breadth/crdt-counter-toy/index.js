#!/usr/bin/env node
/**
 * CRDT G-Counter Toy (failure-first)
 *
 * BREAK — two replicas increment a shared counter independently, then "merge" with last-write-wins:
 *         one replica's increments are lost (classic Google Docs / Figma curveball).
 * FIX   — G-Counter: per-replica counts merge by taking max per slot, then sum → no lost updates.
 */

const args = process.argv.slice(2);
const mode = (args.find((a) => a.startsWith('--mode=')) || '--mode=break').split('=')[1];
const replica = (args.find((a) => a.startsWith('--replica=')) || '--replica=A').split('=')[1];

const INCREMENTS = parseInt(process.env.INCREMENTS || '7', 10);

function naiveCounter() {
  let value = 0;
  const replicas = { A: 0, B: 0 };

  for (let i = 0; i < INCREMENTS; i++) replicas.A += 1;
  for (let i = 0; i < INCREMENTS; i++) replicas.B += 1;

  // Last-write-wins merge: keep only the higher total (loses the other replica's work)
  value = Math.max(replicas.A, replicas.B);

  console.log('=== BREAK: naive shared counter (last-write-wins merge) ===');
  console.log(`Replica A incremented to ${replicas.A}`);
  console.log(`Replica B incremented to ${replicas.B}`);
  console.log(`Merged value (max): ${value}`);
  console.log(`Expected if both counted: ${replicas.A + replicas.B}`);
  console.log(`Lost updates: ${replicas.A + replicas.B - value}`);
}

function gCounterMerge(countsA, countsB) {
  const keys = new Set([...Object.keys(countsA), ...Object.keys(countsB)]);
  let sum = 0;
  for (const k of keys) {
    sum += Math.max(countsA[k] || 0, countsB[k] || 0);
  }
  return sum;
}

function gCounterDemo() {
  const countsA = { A: 0, B: 0 };
  const countsB = { A: 0, B: 0 };

  for (let i = 0; i < INCREMENTS; i++) countsA.A += 1;
  for (let i = 0; i < INCREMENTS; i++) countsB.B += 1;

  const merged = gCounterMerge(countsA, countsB);

  console.log('=== FIX: G-Counter (per-replica slots, merge = max then sum) ===');
  console.log(`Replica A state: ${JSON.stringify(countsA)}`);
  console.log(`Replica B state: ${JSON.stringify(countsB)}`);
  console.log(`Merged total: ${merged}`);
  console.log(`No lost updates — each replica's increments preserved.`);
}

function replicaCli() {
  const slot = replica.toUpperCase();
  const counts = { [slot]: INCREMENTS };
  console.log(`Replica ${slot} local G-Counter: ${JSON.stringify(counts)}`);
  console.log(`Share this JSON with the other terminal; merge with max-per-slot.`);
}

if (mode === 'fix') {
  gCounterDemo();
} else if (mode === 'replica') {
  replicaCli();
} else {
  naiveCounter();
}