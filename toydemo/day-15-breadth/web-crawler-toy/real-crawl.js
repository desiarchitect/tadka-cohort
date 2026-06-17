#!/usr/bin/env node
/**
 * Real HTTP web crawler — naive vs polite + dedup + trap guard.
 *
 * BREAK — no rate limit, no visited set, follows infinite /trap/N links.
 * FIX   — politeness delay, visited URL set, trap depth cap.
 *
 * Prerequisites: Node.js only (starts local site + crawler).
 *
 * Usage:
 *   node real-crawl.js --mode=break
 *   node real-crawl.js --mode=fix
 */

const http = require('http');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const SITE_PORT = parseInt(process.env.SITE_PORT || '31221', 10);
const MAX_PAGES = parseInt(process.env.MAX_PAGES || '200', 10);
const POLITE_DELAY_MS = parseInt(process.env.POLITE_DELAY_MS || '120', 10);
const TRAP_MAX_DEPTH = parseInt(process.env.TRAP_MAX_DEPTH || '3', 10);
const RATE_LIMIT_PER_SEC = parseInt(process.env.SITE_RATE_LIMIT || '8', 10);

const linkRe = /href="([^"]+)"/g;

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

function normalizeUrl(base, href) {
  if (!href || href.startsWith('#') || href.startsWith('mailto:')) return null;
  try {
    const u = new URL(href, base);
    if (u.hostname !== '127.0.0.1' && u.hostname !== 'localhost') return null;
    u.hash = '';
    return u.toString();
  } catch {
    return null;
  }
}

function extractLinks(html, baseUrl) {
  const links = [];
  let m;
  while ((m = linkRe.exec(html)) !== null) {
    const n = normalizeUrl(baseUrl, m[1]);
    if (n) links.push(n);
  }
  return links;
}

function startSiteServer() {
  const window = [];
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const now = Date.now();
      window.push(now);
      while (window.length && now - window[0] > 1000) window.shift();
      if (window.length > RATE_LIMIT_PER_SEC) {
        res.writeHead(429, { 'Retry-After': '1', 'Content-Type': 'text/plain' });
        return res.end('slow down');
      }

      const url = new URL(req.url, `http://127.0.0.1:${SITE_PORT}`);
      const path = url.pathname;

      let body = '';
      if (path === '/') {
        body = `<html><body>
          <h1>Restaurant listings</h1>
          <a href="/catalog/1">catalog 1</a>
          <a href="/dup?n=1">dup a</a>
          <a href="/dup?n=2">dup b</a>
          <a href="/trap/0">calendar trap</a>
        </body></html>`;
      } else if (path.startsWith('/catalog/')) {
        const id = parseInt(path.split('/').pop(), 10) || 1;
        const next = id < 12 ? `<a href="/catalog/${id + 1}">next</a>` : '';
        body = `<html><body><p>menu page ${id}</p>${next}<a href="/">home</a></body></html>`;
      } else if (path === '/dup') {
        body = `<html><body><p>same duplicate body</p>
          <a href="/dup?n=1">self 1</a>
          <a href="/dup?n=2">self 2</a>
          <a href="/">home</a>
        </body></html>`;
      } else if (path.startsWith('/trap/')) {
        const depth = parseInt(path.split('/').pop(), 10) || 0;
        body = `<html><body><p>trap depth ${depth}</p>
          <a href="/trap/${depth + 1}">next month</a>
          <a href="/">home</a>
        </body></html>`;
      } else {
        res.writeHead(404);
        return res.end('missing');
      }

      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end(body);
    });

    server.listen(SITE_PORT, '127.0.0.1', () => resolve(server));
  });
}

function httpGet(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => resolve({
        status: res.statusCode,
        body: Buffer.concat(chunks).toString('utf8')
      }));
    }).on('error', reject);
  });
}

function isTrapUrl(url) {
  return /\/trap\/\d+/.test(url);
}

function trapDepth(url) {
  const m = url.match(/\/trap\/(\d+)/);
  return m ? parseInt(m[1], 10) : 0;
}

async function crawl(opts) {
  const {
    polite,
    dedup,
    trapGuard,
    maxPages
  } = opts;

  const startUrl = `http://127.0.0.1:${SITE_PORT}/`;
  const queue = [startUrl];
  const visited = new Set();
  let fetches = 0;
  let duplicates = 0;
  let duplicateFetches = 0;
  let throttled = 0;
  let trapPages = 0;
  let uniqueContent = new Set();
  const fetchCounts = new Map();

  while (queue.length && fetches < maxPages) {
    const url = queue.shift();
    if (dedup) {
      if (visited.has(url)) {
        duplicates += 1;
        continue;
      }
      visited.add(url);
    } else {
      fetchCounts.set(url, (fetchCounts.get(url) || 0) + 1);
      if (fetchCounts.get(url) > 1) duplicateFetches += 1;
    }

    if (trapGuard && isTrapUrl(url) && trapDepth(url) > TRAP_MAX_DEPTH) {
      continue;
    }

    if (polite && fetches > 0) await sleep(POLITE_DELAY_MS);

    const res = await httpGet(url);
    fetches += 1;

    if (res.status === 429) {
      throttled += 1;
      if (!polite) queue.unshift(url);
      continue;
    }

    if (isTrapUrl(url)) trapPages += 1;

    const contentKey = res.body.replace(/\s+/g, ' ').slice(0, 80);
    if (dedup) {
      if (uniqueContent.has(contentKey)) duplicates += 1;
      else uniqueContent.add(contentKey);
    }

    for (const link of extractLinks(res.body, url)) {
      if (!dedup || !visited.has(link)) queue.push(link);
    }
  }

  return { fetches, duplicates, duplicateFetches, throttled, trapPages, queueRemaining: queue.length };
}

async function main() {
  console.log('=== Web Crawler Toy — Real HTTP ===\n');
  const site = await startSiteServer();
  console.log(`Site server: http://127.0.0.1:${SITE_PORT}/ (rate limit ${RATE_LIMIT_PER_SEC}/s → 429)\n`);

  try {
    if (mode === 'break') {
      console.log('--- BREAK: Naive crawler (fast, no dedup, no trap guard) ---\n');
      const r = await crawl({
        polite: false,
        dedup: false,
        trapGuard: false,
        maxPages: MAX_PAGES
      });
      console.log(`Pages fetched: ${r.fetches}`);
      console.log(`429 throttled: ${r.throttled}`);
      console.log(`Duplicate URL fetches: ${r.duplicateFetches}`);
      console.log(`Trap pages entered: ${r.trapPages}`);
      console.log(`Queue still waiting: ${r.queueRemaining}`);
      console.log('\nWhy this breaks: host blocks you (429), infinite traps burn budget, duplicate URLs re-fetched.');

    } else if (mode === 'fix') {
      console.log(`--- FIX: Polite (${POLITE_DELAY_MS}ms), visited set, trap depth ≤ ${TRAP_MAX_DEPTH} ---\n`);
      const r = await crawl({
        polite: true,
        dedup: true,
        trapGuard: true,
        maxPages: MAX_PAGES
      });
      console.log(`Pages fetched: ${r.fetches}`);
      console.log(`429 throttled: ${r.throttled}`);
      console.log(`Duplicate URLs skipped: ${r.duplicates}`);
      console.log(`Duplicate URL fetches: ${r.duplicateFetches}`);
      console.log(`Trap pages entered: ${r.trapPages}`);
      console.log(`Queue still waiting: ${r.queueRemaining}`);
      console.log('\nWhy this works: politeness avoids 429, dedup saves bandwidth, trap guard stops infinite calendar.');

    } else {
      console.log('Usage: node real-crawl.js --mode=break|fix');
      process.exit(1);
    }
  } finally {
    site.close();
  }
}

main().catch(e => { console.error(e); process.exit(1); });