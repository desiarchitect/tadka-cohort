#!/usr/bin/env node
/**
 * Real Postgres search demo — LIKE seq scan vs GIN full-text index.
 *
 * Postgres GIN on tsvector IS an inverted index under the hood — the same
 * primitive Elasticsearch uses, built into the project's database.
 *
 * Prerequisites:
 *   docker compose up -d postgres   (from tadka/)
 *
 * Usage:
 *   node real-db.js --mode=break
 *   node real-db.js --mode=fix
 */

const { execSync } = require('child_process');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const ROW_TARGET = parseInt(process.env.ROW_TARGET || '100000', 10);
const DOCKER_PSQL = 'docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1';

function runPsql(sql) {
  try {
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

console.log('=== Search Index Toy — Real Postgres Mode ===\n');
console.log('Using the project Postgres (docker service: tadka-postgres).');
console.log('Make sure it is running:  docker compose up -d postgres\n');

console.log('Preparing search corpus (idempotent)...');
runPsql(`
  CREATE TABLE IF NOT EXISTS search_demo (
    id bigserial PRIMARY KEY,
    body text NOT NULL
  );

  DO $$
  BEGIN
    IF (SELECT count(*) FROM search_demo) < ${ROW_TARGET} THEN
      TRUNCATE TABLE search_demo;
      INSERT INTO search_demo (body)
      SELECT
        CASE
          WHEN g % 50 = 0 THEN 'Meghana Biryani Koramangala special #' || g
          ELSE 'Restaurant ' || g || ' serves ' ||
            (ARRAY['biryani','dosa','thali','curry','rolls','pizza','burger'])[1 + (g % 7)] ||
            ' in ' ||
            (ARRAY['koramangala','indiranagar','hsr','whitefield','jayanagar','malleshwaram'])[1 + (g % 6)] ||
            ' bangalore delivery'
        END
      FROM generate_series(1, ${ROW_TARGET}) g;
    END IF;
  END $$;

  ANALYZE search_demo;
`);

const matchCount = runPsql(`
  SELECT count(*) AS matches FROM search_demo
  WHERE body ILIKE '%biryani%' AND body ILIKE '%koramangala%';
`);
console.log('Ground-truth match count (both modes should return this):');
console.log(matchCount.trim());

if (mode === 'break') {
  console.log('\n--- BREAK: LIKE / ILIKE (no search index) ---');
  console.log('Equivalent query:');
  console.log("  SELECT id FROM search_demo");
  console.log("  WHERE body ILIKE '%biryani%' AND body ILIKE '%koramangala%';\n");
  console.log('Watch for: Seq Scan, high "rows removed by Filter", time grows with table size.\n');

  timeQuery('LIKE bad path', `
    SELECT id FROM search_demo
    WHERE body ILIKE '%biryani%' AND body ILIKE '%koramangala%';
  `);

} else if (mode === 'fix') {
  console.log('\n--- FIX: Full-text search + GIN inverted index ---');
  console.log('Building GIN index on to_tsvector(body) if missing...');
  runPsql(`
    CREATE INDEX IF NOT EXISTS idx_search_demo_fts
    ON search_demo USING GIN (to_tsvector('english', body));
    ANALYZE search_demo;
  `);

  console.log('Equivalent query:');
  console.log("  SELECT id FROM search_demo");
  console.log("  WHERE to_tsvector('english', body) @@ to_tsquery('english', 'biryani & koramangala');\n");
  console.log('Watch for: Bitmap Index Scan using GIN, far fewer heap fetches.\n');

  timeQuery('Full-text good path', `
    SELECT id FROM search_demo
    WHERE to_tsvector('english', body) @@ to_tsquery('english', 'biryani & koramangala');
  `);

} else {
  console.log('Usage:');
  console.log('  node real-db.js --mode=break');
  console.log('  node real-db.js --mode=fix');
  process.exit(1);
}

console.log('\nThis is the real planner proof — same Postgres container as Tadka.');
console.log('GIN + tsvector = inverted index; LIKE = seq scan. Elasticsearch is the same idea at scale.');