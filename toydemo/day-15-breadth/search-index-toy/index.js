#!/usr/bin/env node
/**
 * Search / Inverted Index Toy (failure-first demo)
 *
 * Scenario: search 100k restaurant/menu documents for "biryani koramangala".
 *
 * BREAK — SQL LIKE / full table scan: examine every row.
 * FIX   — inverted index: token → posting list, intersect postings.
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const DOC_COUNT = parseInt(process.env.DOC_COUNT || '100000', 10);
const QUERY_TERMS = ['biryani', 'koramangala'];

const AREAS = ['koramangala', 'indiranagar', 'hsr', 'whitefield', 'jayanagar', 'malleshwaram'];
const DISHES = ['biryani', 'dosa', 'thali', 'curry', 'rolls', 'pizza', 'burger'];

function tokenize(text) {
  return text.toLowerCase().split(/\W+/).filter(Boolean);
}

function buildCorpus(count) {
  const docs = [];
  for (let i = 0; i < count; i++) {
    const dish = DISHES[i % DISHES.length];
    const area = AREAS[i % AREAS.length];
    const name = `Restaurant ${i + 1}`;
    const text = `${name} serves ${dish} in ${area} bangalore delivery`;
    docs.push({ id: i + 1, text, dish, area });
  }
  // Plant ~2% matching both query terms in realistic places
  for (let j = 0; j < Math.floor(count * 0.02); j++) {
    const id = 1000 + j * 50;
    if (id < count) {
      docs[id] = {
        id: id + 1,
        text: `Meghana Biryani Koramangala special #${id}`,
        dish: 'biryani',
        area: 'koramangala'
      };
    }
  }
  return docs;
}

function buildInvertedIndex(docs) {
  const index = new Map();
  for (const doc of docs) {
    const tokens = new Set(tokenize(doc.text));
    for (const token of tokens) {
      if (!index.has(token)) index.set(token, []);
      index.get(token).push(doc.id);
    }
  }
  return index;
}

function searchLikeScan(docs, terms) {
  const start = Date.now();
  let examined = 0;
  const hits = [];

  for (const doc of docs) {
    examined += 1;
    const lower = doc.text.toLowerCase();
    if (terms.every(t => lower.includes(t))) {
      hits.push(doc.id);
    }
  }

  return {
    label: 'LIKE scan / full table scan (no index)',
    examined,
    hits: hits.length,
    sampleIds: hits.slice(0, 5),
    queryMs: Date.now() - start
  };
}

function searchInvertedIndex(index, terms) {
  const start = Date.now();
  let examined = 0;
  const postings = terms.map(t => {
    const list = index.get(t) || [];
    examined += list.length;
    return new Set(list);
  });

  if (postings.some(s => s.size === 0)) {
    return {
      label: 'Inverted index (postings intersection)',
      examined,
      hits: 0,
      sampleIds: [],
      queryMs: Date.now() - start
    };
  }

  const [first, ...rest] = postings;
  const hits = [];
  for (const id of first) {
    if (rest.every(s => s.has(id))) hits.push(id);
  }

  return {
    label: 'Inverted index (postings intersection)',
    examined,
    hits: hits.length,
    sampleIds: hits.slice(0, 5),
    queryMs: Date.now() - start
  };
}

function printResult(r) {
  console.log(`Approach:        ${r.label}`);
  console.log(`Docs examined:   ${r.examined.toLocaleString()}`);
  console.log(`Matches found:   ${r.hits.toLocaleString()}`);
  console.log(`Query time:      ${r.queryMs}ms`);
  if (r.sampleIds.length) console.log(`Sample doc ids:  ${r.sampleIds.join(', ')}`);
}

console.log('=== Search / Inverted Index Toy ===');
console.log(`Corpus: ${DOC_COUNT.toLocaleString()} menu/restaurant documents`);
console.log(`Query:  "${QUERY_TERMS.join(' ')}"\n`);

console.log('>>> IMPORTANT: THIS IS A PURE-JS SIMULATION (in-memory inverted index toy).');
console.log('>>> For the REAL lesson — Postgres EXPLAIN with Seq Scan (LIKE) vs GIN full-text:');
console.log('>>>   1. In tadka/:  docker compose up -d postgres');
console.log('>>>   2. Then here:  node real-db.js --mode=break   (then --mode=fix)');
console.log('>>> That shows the actual query planner + rows removed by Filter vs Bitmap Index Scan.');
console.log('>>> See RUN-AND-TEST.md — teach with real-db.js first.\n');

const corpus = buildCorpus(DOC_COUNT);
const index = buildInvertedIndex(corpus);

if (mode === 'break') {
  console.log('--- BREAK: Full scan (LIKE %term%) ---\n');
  printResult(searchLikeScan(corpus, QUERY_TERMS));
  console.log('\nWhy this breaks:');
  console.log('- Every query touches every document — O(n) per search.');
  console.log('- At millions of docs, p99 explodes; CPU pegged.');
  console.log('- B-tree on name column does not help multi-word bag-of-words search.');

} else if (mode === 'fix') {
  console.log('--- FIX: Inverted index ---\n');
  printResult(searchInvertedIndex(index, QUERY_TERMS));
  console.log('\nWhy this works:');
  console.log('- Jump straight to posting lists for biryani AND koramangala.');
  console.log('- Intersect two short lists — work proportional to matches, not corpus size.');
  console.log('- Elasticsearch/Solr core primitive; Tadka uses Postgres OLTP, not this for menu search.');

} else {
  console.log('Usage:');
  console.log('  node index.js --mode=break');
  console.log('  node index.js --mode=fix');
  process.exit(1);
}

console.log('\nCompare docs examined — that is the search scalability story.');