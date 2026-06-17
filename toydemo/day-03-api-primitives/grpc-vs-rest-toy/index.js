#!/usr/bin/env node
/**
 * gRPC vs REST Internal Calls Toy (failure-first demo)
 *
 * Scenario: Ordering must fetch N menu items from Restaurant to price an order.
 *
 * 1. Fast simulation (this file)
 *    - Pure JS, zero dependencies, instant.
 *    - Models chatty REST (N round trips + fat JSON) vs gRPC batch (1 trip + protobuf).
 *
 * 2. Real servers (strongly recommended)
 *    - node real-bench.js --mode=break / --mode=fix
 *    - Spins up actual HTTP + gRPC servers on localhost and measures wall time + bytes.
 *    - Requires: npm install (grpc-js + proto-loader). No Docker.
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const ITEM_COUNT = 200;
const SIMULATED_RTT_MS = 2; // per HTTP round trip on a fast internal network

const CATEGORIES = ['Biryani', 'Dosa', 'Thali', 'Curry', 'Beverage', 'Dessert'];

function buildMenuItem(id) {
  return {
    id,
    restaurantId: 42,
    name: `Menu Item ${id} — ${CATEGORIES[id % CATEGORIES.length]}`,
    pricePaise: 14900 + (id % 50) * 100,
    category: CATEGORIES[id % CATEGORIES.length],
    available: id % 17 !== 0,
    description: `Authentic Bangalore-style dish #${id} with chef notes and allergen metadata.`,
    updatedAt: '2026-06-17T10:00:00.000Z',
    tags: ['veg', 'spicy', 'bestseller'],
    nutrition: { calories: 420 + (id % 80), proteinG: 12, carbsG: 55 }
  };
}

const catalog = Array.from({ length: ITEM_COUNT }, (_, i) => buildMenuItem(i + 1));
const ids = catalog.map(item => item.id);

function restJsonBytes(item) {
  // Verbose JSON field names — typical REST DTO serialization
  return Buffer.byteLength(JSON.stringify({
    id: item.id,
    restaurantId: item.restaurantId,
    name: item.name,
    pricePaise: item.pricePaise,
    category: item.category,
    available: item.available,
    description: item.description,
    updatedAt: item.updatedAt,
    tags: item.tags,
    nutrition: item.nutrition
  }), 'utf8');
}

function protobufEstimateBytes(item) {
  // Wire-size approximation: field tags + varints + short strings (no repeated key names)
  const nameBytes = Buffer.byteLength(item.name, 'utf8');
  const categoryBytes = Buffer.byteLength(item.category, 'utf8');
  const descBytes = Buffer.byteLength(item.description, 'utf8');
  return 8 + nameBytes + 8 + categoryBytes + 8 + descBytes + 16;
}

function simulateRestChatty() {
  let totalBytes = 0;
  let roundTrips = 0;
  const start = Date.now();

  for (const id of ids) {
    roundTrips += 1;
    const item = catalog.find(r => r.id === id);
    totalBytes += restJsonBytes(item);
    // Each GET is its own HTTP request/response cycle
    const spinUntil = Date.now() + SIMULATED_RTT_MS;
    while (Date.now() < spinUntil) { /* simulate network + HTTP framing wait */ }
  }

  return {
    label: 'REST chatty (1 HTTP GET per menu item)',
    roundTrips,
    totalBytes,
    itemsFetched: ids.length,
    wallMs: Date.now() - start
  };
}

function simulateGrpcBatch() {
  const start = Date.now();
  let totalBytes = 0;

  for (const item of catalog) {
    totalBytes += protobufEstimateBytes(item);
  }
  // Single unary RPC — one round trip
  const spinUntil = Date.now() + SIMULATED_RTT_MS;
  while (Date.now() < spinUntil) { /* one RTT */ }

  return {
    label: 'gRPC batch (1 unary GetMenuItemsBatch RPC)',
    roundTrips: 1,
    totalBytes,
    itemsFetched: ids.length,
    wallMs: Date.now() - start
  };
}

function printResult(result) {
  console.log(`Approach:        ${result.label}`);
  console.log(`Items fetched:   ${result.itemsFetched}`);
  console.log(`Round trips:     ${result.roundTrips}`);
  console.log(`Wire bytes:      ${result.totalBytes.toLocaleString()} (~${(result.totalBytes / 1024).toFixed(1)} KB)`);
  console.log(`Simulated time:  ${result.wallMs}ms (RTT=${SIMULATED_RTT_MS}ms per trip)`);
  console.log(`Bytes per item:  ${Math.round(result.totalBytes / result.itemsFetched)}`);
}

console.log('=== gRPC vs REST Internal Calls Toy ===');
console.log(`Workload: price an order needing ${ITEM_COUNT} menu items from Restaurant`);
console.log('(internal service-to-service call — not browser-facing)\n');

console.log('>>> IMPORTANT: THIS IS A PURE-JS SIMULATION (cost model + artificial RTT wait).');
console.log('>>> It shows the *shape*: chatty REST = N round trips + fat JSON; gRPC batch = 1 trip + compact wire.');
console.log('>>> For real HTTP + gRPC servers on localhost with measured wall time + response sizes:');
console.log('>>>   1. npm install');
console.log('>>>   2. node real-bench.js --mode=break   (then --mode=fix)');
console.log('>>> See RUN-AND-TEST.md for the full walkthrough.\n');

if (mode === 'break') {
  console.log('--- BREAK: Chatty REST internal calls ---');
  console.log('Pattern: Ordering loops GET /api/v1/menu-items/{id} for every line item.\n');
  printResult(simulateRestChatty());
  console.log('\nWhy this hurts at scale:');
  console.log('- Latency stacks: N × RTT even when each response is "fast".');
  console.log('- Connection pool pressure: N concurrent internal calls under load.');
  console.log('- JSON verbosity: field names repeated on every object.');
  console.log('- Harder to evolve: every new field bloats every response.');

} else if (mode === 'fix') {
  console.log('--- FIX: gRPC unary batch ---');
  console.log('Pattern: Ordering sends all IDs in one GetMenuItemsBatch RPC.\n');
  printResult(simulateGrpcBatch());
  console.log('\nWhy this wins for internal calls:');
  console.log('- One round trip regardless of item count (within reasonable batch limits).');
  console.log('- Protobuf wire format: no repeated JSON key names, compact integers.');
  console.log('- Strong contract (.proto) — codegen on both sides, fewer drift bugs.');
  console.log('- HTTP/2 multiplexing under the hood when you do need multiple RPCs.');

} else {
  console.log('Usage:');
  console.log('  node index.js --mode=break');
  console.log('  node index.js --mode=fix');
  process.exit(1);
}

console.log('\nRun the other mode and compare round trips + bytes + simulated time.');