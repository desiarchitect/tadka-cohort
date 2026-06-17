#!/usr/bin/env node
/**
 * Real HTTP demo: progressive download vs HLS segments + edge cache.
 *
 * BREAK — each viewer downloads the full 5 MB file from origin.
 * FIX   — each viewer fetches 3 HLS segments (~1.5 MB) via edge; segments cached at edge.
 *
 * Prerequisites: node setup-media.js
 *
 * Usage:
 *   node real-demo.js --mode=break
 *   node real-demo.js --mode=fix
 */

const http = require('http');
const fs = require('fs');
const path = require('path');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const ORIGIN_PORT = parseInt(process.env.ORIGIN_PORT || '31201', 10);
const EDGE_PORT = parseInt(process.env.EDGE_PORT || '31202', 10);
const VIEWERS = parseInt(process.env.VIEWERS || '50', 10);
const HLS_SEGMENTS_PER_VIEWER = 3;
const HLS_START_SEGMENT = 3;

const MEDIA = path.join(__dirname, 'media');

const originStats = { requests: 0, bytes: 0 };
const edgeStats = { requests: 0, bytes: 0, hits: 0, misses: 0 };
const edgeCache = new Map();

function ensureMedia() {
  const playlist = path.join(MEDIA, 'hls', 'playlist.m3u8');
  if (!fs.existsSync(playlist)) {
    console.error('Media not found. Run:  node setup-media.js');
    process.exit(1);
  }
}

function readFileSafe(relPath) {
  const full = path.join(MEDIA, relPath);
  if (!fs.existsSync(full)) return null;
  return fs.readFileSync(full);
}

function startOriginServer() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      let rel = null;
      if (req.url === '/progressive/full.bin') rel = 'progressive/full.bin';
      else if (req.url === '/hls/playlist.m3u8') rel = 'hls/playlist.m3u8';
      else if (req.url && req.url.startsWith('/hls/seg')) rel = 'hls/' + path.basename(req.url);

      const body = rel ? readFileSafe(rel) : null;
      if (!body) {
        res.writeHead(404);
        res.end('not found');
        return;
      }

      originStats.requests += 1;
      originStats.bytes += body.length;
      res.writeHead(200, { 'Content-Length': body.length });
      res.end(body);
    });
    server.listen(ORIGIN_PORT, '127.0.0.1', () => resolve(server));
  });
}

function startEdgeServer() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const cacheKey = req.url;
      edgeStats.requests += 1;

      if (edgeCache.has(cacheKey)) {
        edgeStats.hits += 1;
        const body = edgeCache.get(cacheKey);
        edgeStats.bytes += body.length;
        res.writeHead(200, { 'Content-Length': body.length, 'X-Cache': 'HIT' });
        res.end(body);
        return;
      }

      edgeStats.misses += 1;
      const proxyReq = http.get(`http://127.0.0.1:${ORIGIN_PORT}${req.url}`, (proxyRes) => {
        const chunks = [];
        proxyRes.on('data', c => chunks.push(c));
        proxyRes.on('end', () => {
          const body = Buffer.concat(chunks);
          edgeCache.set(cacheKey, body);
          edgeStats.bytes += body.length;
          res.writeHead(200, { 'Content-Length': body.length, 'X-Cache': 'MISS' });
          res.end(body);
        });
      });
      proxyReq.on('error', () => {
        res.writeHead(502);
        res.end('bad gateway');
      });
    });
    server.listen(EDGE_PORT, '127.0.0.1', () => resolve(server));
  });
}

function httpGet(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => resolve({
        status: res.statusCode,
        bytes: Buffer.concat(chunks).length,
        cache: res.headers['x-cache']
      }));
    }).on('error', reject);
  });
}

async function simulateProgressiveViewers() {
  let clientBytes = 0;
  for (let v = 0; v < VIEWERS; v++) {
    const r = await httpGet(`http://127.0.0.1:${ORIGIN_PORT}/progressive/full.bin`);
    clientBytes += r.bytes;
  }
  return { clientBytes };
}

async function simulateHlsViewers() {
  let clientBytes = 0;
  for (let v = 0; v < VIEWERS; v++) {
    await httpGet(`http://127.0.0.1:${EDGE_PORT}/hls/playlist.m3u8`);
    for (let s = 0; s < HLS_SEGMENTS_PER_VIEWER; s++) {
      const seg = HLS_START_SEGMENT + s;
      const name = `seg${String(seg).padStart(3, '0')}.ts`;
      const r = await httpGet(`http://127.0.0.1:${EDGE_PORT}/hls/${name}`);
      clientBytes += r.bytes;
    }
  }
  return { clientBytes };
}

function mb(n) {
  return (n / 1024 / 1024).toFixed(2);
}

async function main() {
  ensureMedia();

  console.log('=== Video HLS + CDN Toy — Real HTTP Demo ===\n');
  console.log(`Viewers: ${VIEWERS}`);
  console.log(`Origin: http://127.0.0.1:${ORIGIN_PORT}\n`);

  const origin = await startOriginServer();
  let edge = null;

  try {
    if (mode === 'break') {
      console.log('--- BREAK: Progressive download (full file per viewer, no CDN) ---\n');
      const { clientBytes } = await simulateProgressiveViewers();
      console.log(`Client bytes total:     ${mb(clientBytes)} MB (${VIEWERS} viewers × full file)`);
      console.log(`Origin requests:        ${originStats.requests}`);
      console.log(`Origin bytes served:    ${mb(originStats.bytes)} MB`);
      console.log('\nWhy this breaks:');
      console.log('- Every viewer pulls the entire file even to watch 30 seconds.');
      console.log('- Origin egress scales with viewers × file size.');
      console.log('- Seeking/rebuffering re-downloads massive ranges.');

    } else if (mode === 'fix') {
      edge = await startEdgeServer();
      console.log(`Edge CDN:  http://127.0.0.1:${EDGE_PORT}`);
      console.log('--- FIX: HLS segments + edge cache ---\n');
      const { clientBytes } = await simulateHlsViewers();
      console.log(`Client bytes total:     ${mb(clientBytes)} MB (~${HLS_SEGMENTS_PER_VIEWER} segments × ${VIEWERS} viewers)`);
      console.log(`Edge requests:          ${edgeStats.requests} (hits ${edgeStats.hits}, misses ${edgeStats.misses})`);
      console.log(`Edge bytes to clients:  ${mb(edgeStats.bytes)} MB`);
      console.log(`Origin requests:        ${originStats.requests} (only edge cache misses)`);
      console.log(`Origin bytes served:    ${mb(originStats.bytes)} MB`);
      console.log('\nWhy this works:');
      console.log('- Player fetches small segments, not the whole movie.');
      console.log('- Edge caches hot segments — origin sees ~unique segments not ~viewers.');
      console.log('- Adaptive bitrate (not shown here) swaps quality per segment.');

    } else {
      console.log('Usage:');
      console.log('  node setup-media.js');
      console.log('  node real-demo.js --mode=break');
      console.log('  node real-demo.js --mode=fix');
      process.exit(1);
    }
  } finally {
    origin.close();
    if (edge) edge.close();
  }

  console.log('\nAll traffic was real HTTP over localhost with files on disk.');
}

main().catch(err => {
  console.error(err.message || err);
  process.exit(1);
});