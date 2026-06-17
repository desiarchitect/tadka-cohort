#!/usr/bin/env node
/**
 * Cursor Pagination Toy (failure-first demo)
 *
 * This toy exists to make the OFFSET vs Cursor decision concrete.
 *
 * There are two ways to experience it:
 *
 * 1. Fast simulation (this file, default)
 *    - Pure JS arrays, zero dependencies, runs in <1s.
 *    - Teaches the *shape* of the problem: rows examined / cost grows with OFFSET.
 *    - Prints the exact real SQL + what the query planner would roughly show.
 *    - Great for quick demos during teaching.
 *
 * 2. Real Postgres (strongly recommended for understanding "how it actually works")
 *    - Uses the project's existing Postgres (the same container the main Tadka app uses).
 *    - Runs the real queries with EXPLAIN (ANALYZE, BUFFERS, TIMING) + client timing.
 *    - You will see actual Index Scan vs Seq Scan + Filter, "rows removed by filter",
 *      buffer hits/reads, and real execution time differences.
 *    - Run:  node real-db.js --mode=break   then  --mode=fix
 *
 * The JS simulation is the "quick smoke".
 * The real DB verification is where the lesson lands.
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const TOTAL_RECORDS = 100_000;   // Simulate a realistically large table
const PAGE_SIZE = 20;
const HIGH_PAGE = 4000;          // "I'm on page 4000 of my order history"

/**
 * Generate a fake table of records.
 * In a real DB this would be a table with an index on the ORDER BY column.
 */
function generateRecords(count) {
  const records = [];
  for (let i = 1; i <= count; i++) {
    records.push({ id: i, data: `record-${i}` });
  }
  return records;
}

const records = generateRecords(TOTAL_RECORDS);

console.log(`=== Cursor Pagination Toy ===`);
console.log(`Table size: ${TOTAL_RECORDS} rows`);
console.log(`Page size:  ${PAGE_SIZE}`);
console.log(`Deep page example: page ${HIGH_PAGE} (would be OFFSET ${(HIGH_PAGE-1)*PAGE_SIZE})\n`);

console.log('>>> IMPORTANT: THIS IS A PURE-JS SIMULATION (array + artificial cost counter).');
console.log('>>> It shows the *shape* of the problem (OFFSET cost grows linearly; cursor cost is constant).');
console.log('>>> It is deliberately zero-dependency for instant classroom / interview-prep runs.');
console.log('>>> For how it *actually works* in a real database (query planner choice, Index Scan vs Filter,');
console.log('>>> "rows removed by filter", shared buffer hits/reads, real timings):');
console.log('>>>   1. In tadka/ dir:  docker compose up -d postgres');
console.log('>>>   2. Then here:      node real-db.js --mode=break   (then --mode=fix)');
console.log('>>> The real-db.js script runs EXPLAIN (ANALYZE, BUFFERS, TIMING) against the *exact same*');
console.log('>>> Postgres container that the main Tadka app uses. See RUN-AND-TEST.md.\n');

if (mode === 'break') {
  console.log('--- BREAK MODE: OFFSET / LIMIT pagination (the common mistake) ---');
  console.log('Equivalent real SQL:');
  console.log(`  SELECT * FROM orders`);
  console.log(`  ORDER BY id`);
  console.log(`  LIMIT ${PAGE_SIZE} OFFSET ${(HIGH_PAGE-1)*PAGE_SIZE};`);
  console.log('');
  console.log('Why this is bad:');
  console.log('  The database must read (and usually discard) all the rows before the OFFSET.');
  console.log('  For page 4000 that means reading ~80,000 rows just to return 20.');
  console.log('  Cost grows linearly with how deep the user has scrolled.\n');

  const offset = (HIGH_PAGE - 1) * PAGE_SIZE;
  const start = Date.now();

  // === Simulation of what the DB does ===
  // In a real B-tree index + heap table, OFFSET forces the executor to
  // walk the index and then do a heap fetch + discard for every skipped row.
  const page = records.slice(offset, offset + PAGE_SIZE);

  // Artificial "rows examined" cost (this is the important teaching bit)
  let rowsExamined = 0;
  for (let i = 0; i < offset + PAGE_SIZE; i++) {
    rowsExamined++;                    // every row the executor touches
    if (i < offset) {
      // pretend we did a heap fetch + filter + throw the row away
    }
  }

  const duration = Date.now() - start;

  console.log(`Requested page ${HIGH_PAGE}`);
  console.log(`Returned ${page.length} rows (first id=${page[0]?.id}, last id=${page[page.length-1]?.id})`);
  console.log(`Simulated "rows examined / buffers hit": ${rowsExamined}`);
  console.log(`Time (simulated): ${duration}ms\n`);

  console.log('What this looks like in a real Postgres (EXPLAIN ANALYZE):');
  console.log('  - "Seq Scan" or "Index Scan" + "Filter: (rows removed by filter: ~80000)"');
  console.log('  - High "actual rows" vs "rows removed by filter"');
  console.log('  - High "shared hit" + "shared read" buffers for the skipped prefix');
  console.log('  - Actual time and cost that grows with the OFFSET value\n');

  console.log('Real-world symptoms:');
  console.log('  - p99 latency explodes the deeper users scroll');
  console.log('  - Database CPU/IO wasted on rows it will throw away');
  console.log('  - "Load more" at the bottom of long feeds or histories feels broken\n');

  console.log('Run with --mode=fix to see the correct approach.');

} else if (mode === 'fix') {
  console.log('--- FIX MODE: Cursor / Keyset pagination (the correct pattern) ---');
  console.log('Equivalent real SQL (what you actually send from the client):');
  console.log(`  SELECT * FROM orders`);
  console.log(`  WHERE id > $last_seen_id`);
  console.log(`  ORDER BY id`);
  console.log(`  LIMIT ${PAGE_SIZE};`);
  console.log('');
  console.log('The client sends the last id it saw from the previous page.');
  console.log('The database can use the index on (id) to jump directly to the right place.\n');

  const start = Date.now();

  // === Simulation ===
  // We simulate "the user has already fetched up to this point"
  const lastSeenId = (HIGH_PAGE - 1) * PAGE_SIZE;

  const highPage = records.filter(r => r.id > lastSeenId).slice(0, PAGE_SIZE);

  // In the cursor case we only ever look at (page size + 1) rows
  const rowsExamined = PAGE_SIZE + 1;

  const duration = Date.now() - start;

  console.log(`"Jumping" to around page ${HIGH_PAGE} using cursor lastSeenId=${lastSeenId}`);
  console.log(`Returned ${highPage.length} rows (first id=${highPage[0]?.id}, last id=${highPage[highPage.length-1]?.id})`);
  console.log(`Simulated "rows examined / buffers hit": ~${rowsExamined} (constant!)`);
  console.log(`Time (simulated): ${duration}ms\n`);

  console.log('What this looks like in a real Postgres (run the EXPLAIN in the "Real Database Verification" section):');
  console.log('  - "Index Scan using idx_... on ..." + "Limit" (or Index Only Scan)');
  console.log('  - "rows removed by filter: 0" (or extremely low)');
  console.log('  - Very low buffer usage relative to the OFFSET version');
  console.log('  - Actual time stays roughly constant even at high "page numbers"\n');

  console.log('Why this wins:');
  console.log('  - Cost is O(page size), not O(offset + page size)');
  console.log('  - Postgres uses the index to seek directly (Index Scan + Limit)');
  console.log('  - Works for any sort key (usually you use a composite like (created_at, id))');
  console.log('  - Stable performance no matter how deep the user has scrolled\n');

  console.log('Run with --mode=break to compare the bad version.');
  console.log('\nSee RUN-AND-TEST.md for the real Postgres EXPLAIN ANALYZE commands');

} else {
  console.log('Usage:');
  console.log('  node index.js --mode=break');
  console.log('  node index.js --mode=fix');
}

