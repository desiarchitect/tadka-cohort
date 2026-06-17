#!/usr/bin/env node
/**
 * Real demo: menu images in Postgres bytea vs presigned object-store URLs.
 *
 * BREAK — store 50 × 200KB images in Postgres; API must shuttle bytes through DB.
 * FIX   — metadata in Postgres, bytes on object server via presigned token URL.
 *
 * Prerequisites: docker compose up -d postgres
 */

const http = require('http');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const OBJECT_COUNT = parseInt(process.env.OBJECT_COUNT || '50', 10);
const OBJECT_BYTES = 200 * 1024;
const OBJECT_PORT = 31211;
const SECRET = 'toydemo-object-secret';

const OBJECT_DIR = path.join(__dirname, 'objects');
const DOCKER_PSQL = 'docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1';

function runPsql(sql) {
  return execSync(DOCKER_PSQL, {
    encoding: 'utf8',
    input: sql.trim() + '\n',
    stdio: ['pipe', 'pipe', 'pipe'],
    maxBuffer: 16 * 1024 * 1024
  });
}

function makeBlob() {
  const buf = Buffer.alloc(OBJECT_BYTES);
  for (let i = 0; i < OBJECT_BYTES; i++) buf[i] = (i * 17 + 31) & 0xff;
  return buf;
}

function sign(key, expires) {
  return crypto.createHmac('sha256', SECRET).update(`${key}:${expires}`).digest('hex');
}

function ensureObjectsOnDisk() {
  fs.mkdirSync(OBJECT_DIR, { recursive: true });
  for (let i = 1; i <= OBJECT_COUNT; i++) {
    const p = path.join(OBJECT_DIR, `menu-${i}.bin`);
    if (!fs.existsSync(p)) fs.writeFileSync(p, makeBlob());
  }
}

function startObjectServer() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const url = new URL(req.url, 'http://127.0.0.1');
      const key = path.basename(url.pathname);
      const token = url.searchParams.get('token');
      const exp = url.searchParams.get('exp');
      if (!token || !exp || token !== sign(key, exp) || Date.now() > parseInt(exp, 10)) {
        res.writeHead(403);
        return res.end('forbidden');
      }
      const file = path.join(OBJECT_DIR, key);
      if (!fs.existsSync(file)) {
        res.writeHead(404);
        return res.end('missing');
      }
      const body = fs.readFileSync(file);
      res.writeHead(200, { 'Content-Length': body.length });
      res.end(body);
    });
    server.listen(OBJECT_PORT, '127.0.0.1', () => resolve(server));
  });
}

function presignedUrl(key) {
  const exp = String(Date.now() + 60_000);
  const token = sign(key, exp);
  return `http://127.0.0.1:${OBJECT_PORT}/${key}?exp=${exp}&token=${token}`;
}

function httpGet(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode, bytes: Buffer.concat(chunks).length }));
    }).on('error', reject);
  });
}

function seedDbBlobs(blobHex) {
  const values = [];
  for (let i = 1; i <= OBJECT_COUNT; i++) {
    values.push(`('menu-${i}', '${blobHex}'::bytea)`);
  }
  runPsql(`
    CREATE TABLE IF NOT EXISTS menu_images_db (
      id serial PRIMARY KEY,
      name text NOT NULL,
      data bytea NOT NULL
    );
    CREATE TABLE IF NOT EXISTS menu_images_meta (
      id serial PRIMARY KEY,
      name text NOT NULL,
      object_key text NOT NULL
    );
    TRUNCATE menu_images_db, menu_images_meta;
    INSERT INTO menu_images_db (name, data) VALUES ${values.join(',\n')};
  `);
}

async function main() {
  console.log('=== Object Storage Toy — Real Demo ===\n');
  const blob = makeBlob();
  const blobHex = '\\x' + blob.toString('hex');

  ensureObjectsOnDisk();

  if (mode === 'break') {
    console.log('--- BREAK: Blobs inside Postgres (API proxies bytes) ---\n');
    seedDbBlobs(blobHex);

    const sizeRow = runPsql(`
      SELECT pg_size_pretty(pg_total_relation_size('menu_images_db')) AS on_disk,
             pg_size_pretty(sum(octet_length(data))::bigint) AS logical_bytes
      FROM menu_images_db;
    `);
    console.log(sizeRow.trim());

    const start = Date.now();
    const agg = runPsql(`
      EXPLAIN (ANALYZE, TIMING ON)
      SELECT name, octet_length(data) AS bytes
      FROM menu_images_db
      ORDER BY id;
    `);
    const dbMs = Date.now() - start;
    const totalBytes = OBJECT_COUNT * OBJECT_BYTES;

    console.log(agg.trim());
    console.log(`\nRows read from DB: ${OBJECT_COUNT}`);
    console.log(`Bytes that cross app↔DB if API proxies images: ${(totalBytes / 1024 / 1024).toFixed(1)} MB`);
    console.log(`DB read time (EXPLAIN ANALYZE above): ${dbMs}ms`);
    console.log('\nWhy this breaks: backups bloat, memory spikes, API becomes your CDN.');

  } else if (mode === 'fix') {
    const objectServer = await startObjectServer();
    console.log('--- FIX: Presigned URLs to object server ---\n');

    runPsql(`
      CREATE TABLE IF NOT EXISTS menu_images_db (
        id serial PRIMARY KEY,
        name text NOT NULL,
        data bytea NOT NULL
      );
      CREATE TABLE IF NOT EXISTS menu_images_meta (
        id serial PRIMARY KEY,
        name text NOT NULL,
        object_key text NOT NULL
      );
      TRUNCATE menu_images_db, menu_images_meta;
    `);

    const metaValues = [];
    for (let i = 1; i <= OBJECT_COUNT; i++) {
      metaValues.push(`('menu-${i}', 'menu-${i}.bin')`);
    }
    runPsql(`INSERT INTO menu_images_meta (name, object_key) VALUES ${metaValues.join(',\n')};`);

    const metaSize = runPsql(`SELECT pg_size_pretty(pg_total_relation_size('menu_images_meta')) AS size;`);
    console.log(metaSize.trim());

    const start = Date.now();
    let clientBytes = 0;
    for (let i = 1; i <= OBJECT_COUNT; i++) {
      const key = `menu-${i}.bin`;
      const res = await httpGet(presignedUrl(key));
      clientBytes += res.bytes;
    }
    objectServer.close();

    console.log(`Client bytes from object store: ${(clientBytes / 1024 / 1024).toFixed(1)} MB`);
    console.log(`Postgres holds metadata only (see size above)`);
    console.log(`Client fetch time: ${Date.now() - start}ms`);
    console.log('\nWhy this works: bytes bypass app/DB; presigned = time-limited direct access.');

  } else {
    console.log('Usage: node real-demo.js --mode=break|fix');
    process.exit(1);
  }
}

main().catch(e => { console.error(e); process.exit(1); });