#!/usr/bin/env node
// docs/learn/ai-engineering-examples/01-similarity-search.js
//
// Zero-dependency. Proves ONE thing in under a minute: exact-match keyword search
// returns nothing for a natural-language query, while a toy vector-similarity
// search returns ranked, useful results from the SAME data.
//
// This is NOT a real embedding model. Real embeddings (OpenAI, Cohere, a local
// model via Ollama) understand MEANING - they would match "cheap" to "budget"
// or "sasta" to "cheap" even though the words share no letters. This toy only
// counts shared WORDS between the query and each item's text, turned into a
// vector, compared with cosine similarity. Run it, notice where it still falls
// short (see the note it prints about "cheap"), then read ai-engineering.md
// Tier 2 to try the same query against a real model and feel the difference.
//
// Usage:
//   node 01-similarity-search.js "cheap spicy chicken"
//   node 01-similarity-search.js "sweet dessert"

"use strict";

const MENU = [
  { name: "Chicken Biryani", desc: "spicy chicken and basmati rice", veg: false, price: 299 },
  { name: "Veg Biryani", desc: "mild vegetables and basmati rice", veg: true, price: 249 },
  { name: "Butter Chicken", desc: "creamy tomato chicken curry", veg: false, price: 349 },
  { name: "Dal Tadka", desc: "yellow lentils tempered with spices", veg: true, price: 179 },
  { name: "Paneer Tikka", desc: "grilled spicy cottage cheese", veg: true, price: 229 },
  { name: "Chicken 65", desc: "deep fried spicy chicken bites", veg: false, price: 269 },
  { name: "Raita", desc: "cool yogurt with cucumber", veg: true, price: 49 },
  { name: "Gulab Jamun", desc: "sweet fried milk balls in syrup", veg: true, price: 99 },
  { name: "Masala Dosa", desc: "crispy rice crepe with potato filling", veg: true, price: 149 },
  { name: "Chicken Curry", desc: "home style chicken in gravy", veg: false, price: 289 },
  { name: "Veg Fried Rice", desc: "stir fried rice with mixed vegetables", veg: true, price: 199 },
  { name: "Mutton Curry", desc: "slow cooked spicy mutton", veg: false, price: 399 },
  { name: "Papad", desc: "roasted thin lentil wafer", veg: true, price: 29 },
  { name: "Chicken Biryani Family Pack", desc: "spicy chicken and rice, serves four", veg: false, price: 899 },
];

function tokenize(text) {
  return text.toLowerCase().match(/[a-z]+/g) || [];
}

// --- Naive keyword search: what most people build first (a SQL LIKE '%query%'
// in spirit). Matches only if the FULL query appears verbatim in the text.
function keywordSearch(query, menu) {
  const q = query.toLowerCase();
  return menu.filter((item) => (item.name + " " + item.desc).toLowerCase().includes(q));
}

// --- Toy vector similarity: turn the query and each item into a word-count
// vector over the same vocabulary, then rank by cosine similarity. This is
// the MECHANISM real embedding search uses (vector + cosine similarity +
// ranking) - just with "word overlap" standing in for a trained model's
// notion of meaning.
function buildVocab(texts) {
  const vocab = new Set();
  for (const t of texts) for (const w of tokenize(t)) vocab.add(w);
  return Array.from(vocab);
}

function vectorize(text, vocab) {
  const counts = {};
  for (const w of tokenize(text)) counts[w] = (counts[w] || 0) + 1;
  return vocab.map((w) => counts[w] || 0);
}

function cosineSimilarity(a, b) {
  let dot = 0, magA = 0, magB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    magA += a[i] * a[i];
    magB += b[i] * b[i];
  }
  if (magA === 0 || magB === 0) return 0;
  return dot / (Math.sqrt(magA) * Math.sqrt(magB));
}

function similaritySearch(query, menu, topN = 5) {
  const texts = menu.map((item) => item.name + " " + item.desc);
  const vocab = buildVocab([query, ...texts]);
  const queryVec = vectorize(query, vocab);
  const scored = menu.map((item, i) => ({
    item,
    score: cosineSimilarity(queryVec, vectorize(texts[i], vocab)),
  }));
  return scored
    .filter((s) => s.score > 0)
    .sort((a, b) => b.score - a.score)
    .slice(0, topN);
}

function main() {
  const query = process.argv.slice(2).join(" ") || "cheap spicy chicken";
  console.log(`Query: "${query}"\n`);

  console.log("--- Naive keyword search (exact substring match) ---");
  const kw = keywordSearch(query, MENU);
  if (kw.length === 0) {
    console.log("  (0 results) - no menu item's text contains that exact phrase.");
  } else {
    for (const item of kw) console.log(`  ${item.name} - ₹${item.price}`);
  }

  console.log("\n--- Toy vector similarity search (word-overlap, ranked) ---");
  const sim = similaritySearch(query, MENU);
  if (sim.length === 0) {
    console.log("  (0 results) - query shares no words with any item's text.");
  } else {
    for (const { item, score } of sim) {
      console.log(`  ${item.name} - ₹${item.price}  (score ${score.toFixed(2)})`);
    }
  }

  const queryWords = new Set(tokenize(query));
  const allMenuWords = new Set(MENU.flatMap((item) => tokenize(item.name + " " + item.desc)));
  const unmatched = Array.from(queryWords).filter((w) => !allMenuWords.has(w));
  if (unmatched.length > 0) {
    console.log(
      `\nNote: the word(s) [${unmatched.join(", ")}] from your query appear in NO menu text, ` +
      `so they contributed nothing to the ranking above - this toy only counts shared words. ` +
      `A real embedding model would likely still connect "${unmatched[0]}" to price or taste ` +
      `even without an exact word match. That gap is what Tier 2 (a real model) buys you.`
    );
  }
}

main();
