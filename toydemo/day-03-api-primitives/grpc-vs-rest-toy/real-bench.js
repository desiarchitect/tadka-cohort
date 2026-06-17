#!/usr/bin/env node
/**
 * Real HTTP + gRPC benchmark for the gRPC vs REST Internal Calls Toy.
 *
 * Starts lightweight in-memory servers on localhost, runs the same workload
 * as the simulation, and prints measured wall time + wire bytes.
 *
 * Prerequisites: npm install (in this directory)
 *
 * Usage:
 *   node real-bench.js --mode=break
 *   node real-bench.js --mode=fix
 */

const http = require('http');
const path = require('path');
const grpc = require('@grpc/grpc-js');
const protoLoader = require('@grpc/proto-loader');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const ITEM_COUNT = parseInt(process.env.ITEM_COUNT || '200', 10);
const REST_PORT = parseInt(process.env.REST_PORT || '31081', 10);
const GRPC_PORT = parseInt(process.env.GRPC_PORT || '31082', 10);

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

const catalog = new Map();
for (let i = 1; i <= ITEM_COUNT; i++) {
  catalog.set(i, buildMenuItem(i));
}
const ids = [...catalog.keys()];

function startRestServer() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const match = req.url && req.url.match(/^\/api\/v1\/menu-items\/(\d+)$/);
      if (!match) {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'not found' }));
        return;
      }

      const item = catalog.get(parseInt(match[1], 10));
      if (!item) {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'not found' }));
        return;
      }

      const body = JSON.stringify(item);
      res.writeHead(200, {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(body)
      });
      res.end(body);
    });

    server.listen(REST_PORT, '127.0.0.1', () => resolve(server));
  });
}

function loadGrpcDefinition() {
  const protoPath = path.join(__dirname, 'proto', 'menu.proto');
  const packageDefinition = protoLoader.loadSync(protoPath, {
    keepCase: true,
    longs: String,
    enums: String,
    defaults: true,
    oneofs: true
  });
  return grpc.loadPackageDefinition(packageDefinition).menu;
}

function startGrpcServer(menuPkg) {
  return new Promise((resolve) => {
    const impl = {
      GetMenuItemsBatch: (call, callback) => {
        const items = (call.request.ids || [])
          .map(id => catalog.get(parseInt(id, 10)))
          .filter(Boolean);
        callback(null, { items });
      }
    };

    const server = new grpc.Server();
    server.addService(menuPkg.MenuService.service, impl);
    server.bindAsync(
      `127.0.0.1:${GRPC_PORT}`,
      grpc.ServerCredentials.createInsecure(),
      (err) => {
        if (err) throw err;
        resolve(server);
      }
    );
  });
}

function httpGet(url) {
  return new Promise((resolve, reject) => {
    const req = http.get(url, (res) => {
      const chunks = [];
      res.on('data', chunk => chunks.push(chunk));
      res.on('end', () => {
        const body = Buffer.concat(chunks);
        resolve({ status: res.statusCode, bytes: body.length });
      });
    });
    req.on('error', reject);
  });
}

function runRestChattyBenchmark() {
  return new Promise(async (resolve, reject) => {
    try {
      let totalBytes = 0;
      let roundTrips = 0;
      const start = Date.now();

      for (const id of ids) {
        const result = await httpGet(`http://127.0.0.1:${REST_PORT}/api/v1/menu-items/${id}`);
        if (result.status !== 200) {
          throw new Error(`REST GET failed for id=${id} status=${result.status}`);
        }
        roundTrips += 1;
        totalBytes += result.bytes;
      }

      resolve({
        label: 'REST chatty (sequential GET per item)',
        roundTrips,
        totalBytes,
        itemsFetched: ids.length,
        wallMs: Date.now() - start
      });
    } catch (err) {
      reject(err);
    }
  });
}

function runGrpcBatchBenchmark(menuPkg) {
  return new Promise((resolve, reject) => {
    const client = new menuPkg.MenuService(
      `127.0.0.1:${GRPC_PORT}`,
      grpc.credentials.createInsecure()
    );

    const start = Date.now();
    client.GetMenuItemsBatch({ ids }, (err, response) => {
      if (err) return reject(err);

      const items = response.items || [];
      const totalBytes = Buffer.byteLength(JSON.stringify(items));
      // JSON.stringify is only for a rough size proxy in the report;
      // on the wire gRPC uses protobuf (typically smaller). The timing is the real win.

      resolve({
        label: 'gRPC batch (one GetMenuItemsBatch RPC)',
        roundTrips: 1,
        totalBytes,
        itemsFetched: items.length,
        wallMs: Date.now() - start,
        note: 'Byte count shown is decoded-object JSON proxy; protobuf wire is smaller.'
      });
    });
  });
}

function printResult(result) {
  console.log(`Approach:      ${result.label}`);
  console.log(`Items fetched: ${result.itemsFetched}`);
  console.log(`Round trips:   ${result.roundTrips}`);
  console.log(`Response bytes:${result.totalBytes.toLocaleString()} (~${(result.totalBytes / 1024).toFixed(1)} KB)`);
  console.log(`Wall time:     ${result.wallMs}ms`);
  if (result.note) console.log(`Note:          ${result.note}`);
}

async function main() {
  console.log('=== gRPC vs REST Toy — Real Server Benchmark ===\n');
  console.log(`Workload: fetch ${ITEM_COUNT} menu items`);
  console.log(`REST server: http://127.0.0.1:${REST_PORT}`);
  console.log(`gRPC server: 127.0.0.1:${GRPC_PORT}\n`);

  const menuPkg = loadGrpcDefinition();
  const restServer = await startRestServer();
  const grpcServer = await startGrpcServer(menuPkg);

  try {
    if (mode === 'break') {
      console.log('--- BREAK: Chatty REST ---\n');
      printResult(await runRestChattyBenchmark());
    } else if (mode === 'fix') {
      console.log('--- FIX: gRPC batch ---\n');
      printResult(await runGrpcBatchBenchmark(menuPkg));
    } else {
      console.log('Usage:');
      console.log('  node real-bench.js --mode=break');
      console.log('  node real-bench.js --mode=fix');
      process.exit(1);
    }
  } finally {
    await new Promise(r => restServer.close(r));
    grpcServer.tryShutdown(() => {});
  }

  console.log('\nCompare with: node index.js --mode=break / --mode=fix (simulation).');
  console.log('The round-trip count gap is the headline; wall time proves it on real sockets.');
}

main().catch(err => {
  console.error('Benchmark failed:', err.message || err);
  process.exit(1);
});