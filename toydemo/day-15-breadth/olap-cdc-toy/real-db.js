#!/usr/bin/env node
/**
 * Real Postgres OLAP demo — aggregate on row-oriented fact vs CDC-fed rollup.
 *
 * BREAK — scan millions of OLTP-style fact rows every dashboard refresh.
 * FIX   — query pre-aggregated rollup (what CDC stream + columnar warehouse feeds).
 *
 * Prerequisites: docker compose up -d postgres
 *
 * Usage:
 *   node real-db.js --mode=break
 *   node real-db.js --mode=fix
 */

const { execSync } = require('child_process');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];
const ROW_TARGET = parseInt(process.env.ROW_TARGET || '500000', 10);
const DOCKER_PSQL = 'docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1';

function runPsql(sql) {
  try {
    return execSync(DOCKER_PSQL, {
      encoding: 'utf8',
      input: sql.trim() + '\n',
      stdio: ['pipe', 'pipe', 'pipe']
    });
  } catch (e) {
    console.error('Error running psql:');
    console.error(e.stdout || e.stderr || e.message);
    process.exit(1);
  }
}

function timeQuery(label, sql) {
  const start = Date.now();
  const explain = runPsql(`EXPLAIN (ANALYZE, BUFFERS, TIMING ON) ${sql}`);
  console.log(`\n[${label}]`);
  console.log(`Client time: ${Date.now() - start}ms`);
  console.log(explain);
}

console.log('=== OLAP / CDC Toy — Real Postgres Mode ===\n');
console.log('Using tadka-postgres.  docker compose up -d postgres\n');

console.log('Preparing analytics tables (idempotent)...');
runPsql(`
  CREATE TABLE IF NOT EXISTS analytics_fact (
    id bigserial PRIMARY KEY,
    region text NOT NULL,
    amount numeric(10,2) NOT NULL,
    created_at timestamptz NOT NULL
  );

  CREATE TABLE IF NOT EXISTS analytics_rollup (
    region text NOT NULL,
    day date NOT NULL,
    total_amount numeric(14,2) NOT NULL DEFAULT 0,
    order_count bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (region, day)
  );

  DO $$
  BEGIN
    IF (SELECT count(*) FROM analytics_fact) < ${ROW_TARGET} THEN
      TRUNCATE TABLE analytics_fact;
      TRUNCATE TABLE analytics_rollup;

      INSERT INTO analytics_fact (region, amount, created_at)
      SELECT
        (ARRAY['koramangala','indiranagar','hsr','whitefield'])[1 + (g % 4)],
        (random() * 900 + 100)::numeric(10,2),
        now() - (g % 30) * interval '1 day' - (random() * interval '1 day')
      FROM generate_series(1, ${ROW_TARGET}) g;

      INSERT INTO analytics_rollup (region, day, total_amount, order_count)
      SELECT region, created_at::date, SUM(amount), COUNT(*)
      FROM analytics_fact
      GROUP BY region, created_at::date;

      CREATE INDEX IF NOT EXISTS idx_analytics_fact_created ON analytics_fact(created_at);
    END IF;
  END $$;

  ANALYZE analytics_fact;
  ANALYZE analytics_rollup;
`);

const factCount = runPsql('SELECT count(*) AS fact_rows FROM analytics_fact;').trim();
const rollupCount = runPsql('SELECT count(*) AS rollup_rows FROM analytics_rollup;').trim();
console.log(factCount);
console.log(rollupCount);

const queryWindow = `
  WHERE created_at >= now() - interval '7 days'
`;

const rollupWindow = `
  WHERE day >= (current_date - interval '7 days')::date
`;

if (mode === 'break') {
  console.log('\n--- BREAK: OLAP query on row-oriented fact table ---');
  console.log('Dashboard SQL (scans OLTP fact rows):');
  console.log('  SELECT region, SUM(amount), COUNT(*) FROM analytics_fact');
  console.log('  WHERE created_at >= now() - interval \'7 days\' GROUP BY region;\n');

  timeQuery('Row-store aggregate', `
    SELECT region, SUM(amount) AS revenue, COUNT(*) AS orders
    FROM analytics_fact
    ${queryWindow}
    GROUP BY region
    ORDER BY revenue DESC;
  `);

  console.log('Why this breaks on OLTP Postgres:');
  console.log('- Every dashboard refresh scans huge fact heap.');
  console.log('- Competes with live order writes (Tadka OLTP).');
  console.log('- This is why analytics moves to CDC → warehouse/columnar.');

} else if (mode === 'fix') {
  console.log('\n--- FIX: Same dashboard on CDC-fed rollup ---');
  console.log('(Rollup rows = what a CDC consumer maintains from order events)\n');

  timeQuery('Rollup aggregate', `
    SELECT region, SUM(total_amount) AS revenue, SUM(order_count) AS orders
    FROM analytics_rollup
    ${rollupWindow}
    GROUP BY region
    ORDER BY revenue DESC;
  `);

  console.log('Why this works:');
  console.log('- Rollup has thousands of rows, not hundreds of thousands.');
  console.log('- CDC stream appends/upserts — dashboard reads never touch hot OLTP.');
  console.log('- Columnar warehouses (BigQuery/ClickHouse) take this further at scale.');

} else {
  console.log('Usage: node real-db.js --mode=break|fix');
  process.exit(1);
}

console.log('\nOptional: node real-cdc.js — watch CDC jsonl append into rollup live.');