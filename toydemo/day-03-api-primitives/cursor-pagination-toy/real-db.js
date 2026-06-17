#!/usr/bin/env node
/**
 * Real Database verification for the Cursor Pagination Toy.
 *
 * This script runs the actual queries against the project's Postgres
 * (the same docker service used by the main Tadka app) and shows:
 *   - Timing
 *   - EXPLAIN (ANALYZE, BUFFERS) output
 *
 * This is the version that makes "how it actually works" obvious
 * (index usage, rows examined, buffer/IO cost, query planner decisions).
 *
 * Prerequisites:
 *   - The project's docker-compose Postgres must be running:
 *       docker compose up -d postgres
 *
 * Usage:
 *   node real-db.js --mode=break
 *   node real-db.js --mode=fix
 */

const { execSync } = require('child_process');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const PAGE_SIZE = 20;
const HIGH_OFFSET = 80000;   // page ~4000

const DOCKER_PSQL = 'docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1';

function runPsql(sql) {
  try {
    // Pipe SQL via stdin — avoids Windows shell breaking multi-line -c strings.
    const result = execSync(DOCKER_PSQL, {
      encoding: 'utf8',
      input: sql.trim() + '\n',
      stdio: ['pipe', 'pipe', 'pipe']
    });
    return result;
  } catch (e) {
    console.error('Error running psql:');
    console.error(e.stdout || e.stderr || e.message);
    process.exit(1);
  }
}

function timeQuery(label, sql) {
  const start = Date.now();
  const explain = runPsql(`EXPLAIN (ANALYZE, BUFFERS, TIMING ON) ${sql}`);
  const duration = Date.now() - start;
  console.log(`\n[${label}]`);
  console.log(`Actual client time: ${duration}ms`);
  console.log(explain);
}

console.log('=== Cursor Pagination Toy — Real Database Mode ===\n');
console.log('Using the project\'s Postgres (docker service: tadka-postgres).');
console.log('Make sure it is running:  docker compose up -d postgres\n');

// Ensure we have a decent sized test table with an index on the sort column.
console.log('Preparing test data (idempotent)...');
runPsql(`
  CREATE TABLE IF NOT EXISTS pagination_demo (
    id bigserial PRIMARY KEY,
    payload text
  );

  -- Only insert if the table is empty or too small
  DO $$
  BEGIN
    IF (SELECT count(*) FROM pagination_demo) < 90000 THEN
      TRUNCATE TABLE pagination_demo;
      INSERT INTO pagination_demo (payload)
      SELECT 'row-' || g FROM generate_series(1, 100000) g;
    END IF;
  END $$;

  CREATE INDEX IF NOT EXISTS idx_pagination_demo_id ON pagination_demo(id);
  ANALYZE pagination_demo;
`);

if (mode === 'break') {
  console.log('--- BREAK: OFFSET version (the expensive one) ---');
  console.log('Equivalent query:');
  console.log(`  SELECT * FROM pagination_demo ORDER BY id LIMIT ${PAGE_SIZE} OFFSET ${HIGH_OFFSET};`);
  console.log('\nYou should see high "rows removed by filter", high buffer usage,');
  console.log('and time that grows with the OFFSET.\n');

  timeQuery('OFFSET bad path', `
    SELECT * FROM pagination_demo
    ORDER BY id
    LIMIT ${PAGE_SIZE} OFFSET ${HIGH_OFFSET};
  `);

} else if (mode === 'fix') {
  console.log('--- FIX: Cursor / Keyset version (the cheap one) ---');
  console.log('Equivalent query (client sends the last id it saw):');
  console.log(`  SELECT * FROM pagination_demo WHERE id > ${HIGH_OFFSET} ORDER BY id LIMIT ${PAGE_SIZE};`);
  console.log('\nYou should see a clean Index Scan + Limit, very low rows examined,');
  console.log('and time that stays roughly constant even at high "page" numbers.\n');

  timeQuery('Cursor good path', `
    SELECT * FROM pagination_demo
    WHERE id > ${HIGH_OFFSET}
    ORDER BY id
    LIMIT ${PAGE_SIZE};
  `);

} else {
  console.log('Usage:');
  console.log('  node real-db.js --mode=break');
  console.log('  node real-db.js --mode=fix');
}

console.log('\nTip: compare the "actual time", "rows removed by filter", and "shared hit/read" buffers between the two modes.');
console.log('This is the real behavior the JS simulation is trying to teach.');