#!/usr/bin/env node
/**
 * Stream Processing / Heavy Hitters Toy (failure-first demo)
 *
 * Scenario: live leaderboard — "top restaurants by clicks in the last 5 minutes"
 * during a cricket-match surge (Swiggy dinner rush archetype).
 *
 * BREAK — store every click forever + full recount: unbounded memory, stale leaders.
 * FIX   — tumbling 1-minute buckets, keep last 5: bounded state, correct recent top-K.
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const EVENTS = parseInt(process.env.EVENTS || '100000', 10);
const WINDOW_MINUTES = 5;
const BUCKET_MINUTES = 1;
const TOP_K = 3;
const RESTAURANTS = ['Meghana', 'Truffles', 'Nandhini', 'CornerHouse', 'Magnolia'];

function generateEvents(count) {
  const events = [];
  for (let i = 0; i < count; i++) {
    const minute = Math.floor((i / count) * 10); // 10-minute simulation
    let restaurant;
    if (minute <= 5) {
      restaurant = Math.random() < 0.85 ? 'CornerHouse' : RESTAURANTS[i % RESTAURANTS.length];
    } else {
      restaurant = Math.random() < 0.92 ? 'Meghana' : 'Truffles';
    }
    events.push({ id: i, restaurant, minute });
  }
  return events;
}

function topKFromCounts(counts, k) {
  return Object.entries(counts)
    .sort((a, b) => b[1] - a[1])
    .slice(0, k)
    .map(([name, c]) => `${name}:${c}`);
}

function simulateUnbounded(events) {
  const allClicks = [];
  let recountMs = 0;
  let queryAtEnd = [];

  for (let i = 0; i < events.length; i++) {
    allClicks.push(events[i]);
    if (i % 5000 === 0 || i === events.length - 1) {
      const start = Date.now();
      const counts = {};
      for (const e of allClicks) {
        counts[e.restaurant] = (counts[e.restaurant] || 0) + 1;
      }
      recountMs += Date.now() - start;
      if (i === events.length - 1) {
        queryAtEnd = topKFromCounts(counts, TOP_K);
      }
    }
  }

  const currentMinute = events[events.length - 1].minute;
  const windowStart = currentMinute - WINDOW_MINUTES;
  const recentOnly = events.filter(e => e.minute >= windowStart);
  const recentCounts = {};
  for (const e of recentOnly) {
    recentCounts[e.restaurant] = (recentCounts[e.restaurant] || 0) + 1;
  }
  const correctTop = topKFromCounts(recentCounts, TOP_K);

  return {
    label: 'Unbounded store + full recount (no window)',
    eventsProcessed: events.length,
    stateSize: allClicks.length,
    recountCpuMs: recountMs,
    reportedTop: queryAtEnd,
    correctTopForLast5Min: correctTop,
    leaderCorrect: queryAtEnd[0] === correctTop[0]
  };
}

function simulateWindowed(events) {
  const buckets = new Map(); // minute -> { restaurant: count }
  let updateMs = 0;
  let queryAtEnd = [];

  function prune(nowMinute) {
    for (const key of buckets.keys()) {
      if (key < nowMinute - WINDOW_MINUTES) buckets.delete(key);
    }
  }

  function aggregate() {
    const totals = {};
    for (const counts of buckets.values()) {
      for (const [r, c] of Object.entries(counts)) {
        totals[r] = (totals[r] || 0) + c;
      }
    }
    return totals;
  }

  for (let i = 0; i < events.length; i++) {
    const e = events[i];
    const start = Date.now();
    const bucketKey = Math.floor(e.minute / BUCKET_MINUTES) * BUCKET_MINUTES;
    if (!buckets.has(bucketKey)) buckets.set(bucketKey, {});
    const b = buckets.get(bucketKey);
    b[e.restaurant] = (b[e.restaurant] || 0) + 1;
    prune(e.minute);
    updateMs += Date.now() - start;

    if (i === events.length - 1) {
      queryAtEnd = topKFromCounts(aggregate(), TOP_K);
    }
  }

  const currentMinute = events[events.length - 1].minute;
  const windowStart = currentMinute - WINDOW_MINUTES;
  const recentOnly = events.filter(e => e.minute >= windowStart);
  const recentCounts = {};
  for (const e of recentOnly) {
    recentCounts[e.restaurant] = (recentCounts[e.restaurant] || 0) + 1;
  }
  const correctTop = topKFromCounts(recentCounts, TOP_K);

  return {
    label: `Tumbling ${BUCKET_MINUTES}m buckets, keep last ${WINDOW_MINUTES}m`,
    eventsProcessed: events.length,
    stateSize: buckets.size,
    recountCpuMs: updateMs,
    reportedTop: queryAtEnd,
    correctTopForLast5Min: correctTop,
    leaderCorrect: queryAtEnd[0] === correctTop[0]
  };
}

function printResult(r) {
  console.log(`Approach:           ${r.label}`);
  console.log(`Events processed:   ${r.eventsProcessed.toLocaleString()}`);
  console.log(`State size:         ${r.stateSize.toLocaleString()} ${r.stateSize === r.eventsProcessed ? 'events (unbounded!)' : 'minute buckets'}`);
  console.log(`CPU time (updates): ${r.recountCpuMs}ms`);
  console.log(`Reported top-${TOP_K}:     ${r.reportedTop.join(', ')}`);
  console.log(`Correct top-${TOP_K}:      ${r.correctTopForLast5Min.join(', ')} (last ${WINDOW_MINUTES} min ground truth)`);
  console.log(`Leader correct:     ${r.leaderCorrect ? 'YES' : 'NO — stale/wrong window'}`);
}

console.log('=== Stream Processing / Heavy Hitters Toy ===');
console.log(`Live leaderboard: top-${TOP_K} restaurants by clicks in last ${WINDOW_MINUTES} minutes`);
console.log(`Simulated cricket-match surge → Meghana hot cell\n`);

console.log('>>> IMPORTANT: PURE-JS SIMULATION (click stream + leaderboard query).');
console.log('>>> Break = unbounded memory + all-time counts. Fix = sliding/tumbling window.');
console.log('>>> See RUN-AND-TEST.md.\n');

const events = generateEvents(EVENTS);

if (mode === 'break') {
  console.log('--- BREAK: Unbounded event log + full recount ---\n');
  printResult(simulateUnbounded(events));
  console.log('\nWhy this breaks:');
  console.log('- Memory grows with every click — OOM under real ad-tech volume.');
  console.log('- Full recount cost grows linearly — consumer lag explodes.');
  console.log('- All-time totals answer the wrong question (interview asks "last 5 min").');

} else if (mode === 'fix') {
  console.log('--- FIX: Tumbling buckets + prune old windows ---\n');
  printResult(simulateWindowed(events));
  console.log('\nWhy this works:');
  console.log('- State bounded to window size (5 buckets here), not event count.');
  console.log('- O(1) update per event; query scans only live buckets.');
  console.log('- Same pattern as Kafka windowed aggregation / Redis sorted sets with TTL.');

} else {
  console.log('Usage:');
  console.log('  node index.js --mode=break');
  console.log('  node index.js --mode=fix');
  process.exit(1);
}

console.log('\nCompare state size and whether the reported leader matches the last-5-min truth.');