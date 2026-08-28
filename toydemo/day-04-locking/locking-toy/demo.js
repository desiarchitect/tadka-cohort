#!/usr/bin/env node
/**
 * Day 4 locking toy — real Postgres, same tadka-postgres container.
 * Does NOT touch ordering.orders. Orders stay optimistic (xmin / 409).
 *
 * Two terminals. Start `hold`, then immediately the other command.
 *
 *   node demo.js hold
 *   node demo.js wait      # blocks ~5s
 *   node demo.js nowait    # instant error
 *   node demo.js skip      # takes id=2
 *   node demo.js deadlock-a / deadlock-b   # leftover
 */

const { execSync } = require("node:child_process");

const MODE = process.argv[2] || "help";
const DOCKER_PSQL = "docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1";

function psql(sql, timeoutMs) {
  try {
    return execSync(DOCKER_PSQL, {
      encoding: "utf8",
      input: sql.trim() + "\n",
      stdio: ["pipe", "pipe", "pipe"],
      timeout: timeoutMs || 20000,
    });
  } catch (e) {
    const msg = (e.stderr || e.stdout || e.message || "").toString();
    console.error(msg);
    process.exit(e.status === 1 || e.status === 3 ? 1 : 1);
  }
}

const SETUP = `
CREATE TABLE IF NOT EXISTS locking_demo (
  id int PRIMARY KEY,
  label text NOT NULL
);
INSERT INTO locking_demo (id, label) VALUES (1, 'row-1'), (2, 'row-2')
  ON CONFLICT (id) DO NOTHING;
`;

const MODES = {
  hold: {
    timeout: 15000,
    sql: `
${SETUP}
\\echo HOLD: locking id=1 for 5 seconds (FOR UPDATE + pg_sleep). Other terminal: wait / nowait / skip.
BEGIN;
SELECT id, label FROM locking_demo WHERE id = 1 FOR UPDATE;
SELECT pg_sleep(5);
COMMIT;
\\echo HOLD: released id=1
`,
  },
  wait: {
    timeout: 15000,
    sql: `
${SETUP}
\\timing on
\\echo WAIT: FOR UPDATE on id=1 — if hold is running this BLOCKS, it does not error.
BEGIN;
SELECT id, label FROM locking_demo WHERE id = 1 FOR UPDATE;
COMMIT;
\\echo WAIT: got the lock (Time includes the wait)
`,
  },
  nowait: {
    timeout: 8000,
    sql: `
${SETUP}
\\echo NOWAIT: FOR UPDATE NOWAIT on id=1 — instant error if hold is running.
BEGIN;
SELECT id, label FROM locking_demo WHERE id = 1 FOR UPDATE NOWAIT;
COMMIT;
`,
  },
  skip: {
    timeout: 8000,
    sql: `
${SETUP}
\\echo SKIP: FOR UPDATE SKIP LOCKED LIMIT 1 — skips locked id=1, takes id=2.
BEGIN;
SELECT id, label FROM locking_demo ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 1;
COMMIT;
`,
  },
  "deadlock-a": {
    timeout: 15000,
    sql: `
${SETUP}
\\echo DEADLOCK-A: lock 1, sleep 2s, lock 2. Start deadlock-b in the other window NOW.
BEGIN;
SELECT id FROM locking_demo WHERE id = 1 FOR UPDATE;
SELECT pg_sleep(2);
SELECT id FROM locking_demo WHERE id = 2 FOR UPDATE;
COMMIT;
\\echo DEADLOCK-A: committed (the other session was aborted)
`,
  },
  "deadlock-b": {
    timeout: 15000,
    sql: `
${SETUP}
\\echo DEADLOCK-B: lock 2, sleep 2s, lock 1.
BEGIN;
SELECT id FROM locking_demo WHERE id = 2 FOR UPDATE;
SELECT pg_sleep(2);
SELECT id FROM locking_demo WHERE id = 1 FOR UPDATE;
COMMIT;
\\echo DEADLOCK-B: committed
`,
  },
};

function help() {
  console.log(`Day 4 locking toy — Postgres FOR UPDATE (not Tadka orders).

Prereq: docker compose up -d   (container tadka-postgres)

Two PowerShell windows, repo-relative:

  cd toydemo\\day-04-locking\\locking-toy

Window A:  node demo.js hold
Window B (immediately):
  node demo.js wait      # blocks ~5s, then proceeds
  node demo.js nowait    # could not obtain lock
  node demo.js skip      # returns id=2

Leftover: node demo.js deadlock-a   and   deadlock-b
`);
}

if (MODE === "help" || MODE === "-h" || MODE === "--help" || !MODES[MODE]) {
  help();
  if (MODE !== "help" && MODE !== "-h" && MODE !== "--help") {
    console.error(`Unknown mode '${MODE}'.`);
    process.exit(1);
  }
  process.exit(0);
}

console.log(`=== locking-toy  mode=${MODE}  (tadka-postgres) ===\n`);
const started = Date.now();
const out = psql(MODES[MODE].sql, MODES[MODE].timeout);
process.stdout.write(out);
console.log(`\n[client wall clock: ${Date.now() - started}ms]`);
