// Day 4 break kit, Demo 5 (ADR-045): fires N concurrent redeem requests at the seeded
// TADKA50 coupon (100 max) against one of three strategies and reports what actually
// landed in the database - the numbers the break-kit captures.
//
// Usage:
//   node scripts/coupon-concurrency-demo.js none        # BROKEN: lost updates + oversell
//   node scripts/coupon-concurrency-demo.js optimistic  # correct, but a 409 retry storm
//   node scripts/coupon-concurrency-demo.js pessimistic # correct, no retries needed
//
// Requires: the API running on :5224 (or BASE_URL) and postgres reachable via `docker exec
// tadka-postgres psql` for the verification query (skipped if docker is unavailable).

const { execSync } = require("node:child_process");

const BASE_URL = process.env.BASE_URL || "http://localhost:5224";
const STRATEGY = process.argv[2] || "none";
const CONCURRENCY = parseInt(process.argv[3] || "50", 10);
const CODE = "TADKA50";

if (!["none", "optimistic", "pessimistic"].includes(STRATEGY)) {
  console.error(`Unknown strategy '${STRATEGY}'. Use none | optimistic | pessimistic.`);
  process.exit(1);
}

function uuid() {
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

async function redeemOnce() {
  const started = Date.now();
  try {
    const res = await fetch(`${BASE_URL}/api/v1/coupons/${CODE}/redeem/${STRATEGY}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ customerId: uuid() }),
    });
    return { status: res.status, ms: Date.now() - started };
  } catch (e) {
    return { status: "ERR", ms: Date.now() - started, error: e.message };
  }
}

async function reset() {
  await fetch(`${BASE_URL}/api/v1/coupons/${CODE}/reset`, { method: "POST" });
}

function dbCount(sql) {
  try {
    const out = execSync(
      `docker exec tadka-postgres psql -U tadka -d tadka -t -c "${sql}"`,
      { encoding: "utf8" }
    );
    return out.trim();
  } catch {
    return "(docker/psql unavailable - skipped)";
  }
}

async function main() {
  console.log(`Resetting ${CODE}...`);
  await reset();

  console.log(`Firing ${CONCURRENCY} concurrent redeem requests, strategy=${STRATEGY}...`);
  const startedAll = Date.now();
  const results = await Promise.all(Array.from({ length: CONCURRENCY }, redeemOnce));
  const totalMs = Date.now() - startedAll;

  const byStatus = {};
  for (const r of results) byStatus[r.status] = (byStatus[r.status] || 0) + 1;

  console.log("\n--- Response codes ---");
  for (const [status, count] of Object.entries(byStatus)) {
    console.log(`  ${status}: ${count}`);
  }
  console.log(`\nWall clock for ${CONCURRENCY} concurrent requests: ${totalMs}ms`);

  const redeemedCounter = dbCount(`SELECT \\"Redeemed\\" FROM ordering.coupons WHERE \\"Code\\"='${CODE}';`);
  const redemptionRows = dbCount(`SELECT COUNT(*) FROM ordering.coupon_redemptions;`);

  console.log("\n--- Database ground truth ---");
  console.log(`  coupons.Redeemed counter: ${redeemedCounter}`);
  console.log(`  coupon_redemptions rows : ${redemptionRows}`);
  console.log(
    `\nExpected if correct: both equal min(${CONCURRENCY}, 100) with 0 duplicate/lost-update` +
      ` drift between the counter and the row count.`
  );
}

main();
