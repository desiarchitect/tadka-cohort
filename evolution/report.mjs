#!/usr/bin/env node
// Tadka evolution report: turns evolution/results/*.json (written by k6/evolution.js through
// run-checkpoint.ps1/.sh) into evolution/report.md + one chart pair per scenario in evolution/report/.
//
//   node evolution/report.mjs                       # default: 0.1 requests/s per active user
//   node evolution/report.mjs --rps-per-user 0.05   # change the "users supported" assumption
//   node evolution/report.mjs --results <dir> --out <dir>
//
// No dependencies. Repeats (-r1, -r2, ...) of the same label are folded into their median run.
// The charts are plain SVG files (GitHub renders them via the image tag, and they follow the reader's
// light/dark preference). Every number in a chart is also in the report's tables.

import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name, dflt) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : dflt;
};
const RESULTS = opt('results', join(HERE, 'results'));
const OUT = opt('out', HERE);
const RPS_PER_USER = parseFloat(opt('rps-per-user', '0.1'));
const CHART_DIR = join(OUT, 'report');

const manifest = JSON.parse(readFileSync(join(HERE, 'checkpoints.json'), 'utf8'));
const DAY_ORDER = Object.keys(manifest.checkpoints);
const SCENARIOS = {
  history: { title: 'Order history on 200k orders', proves: 'indexes, pool sizing, read replica (Day 5) and scale-out (Day 6/16)' },
  hotkey: { title: 'Hot key: 80% of reads on one menu', proves: 'Redis cache-aside + single-flight stampede lock (Day 6)' },
  browse: { title: 'Everyday browse (list + menu)', proves: 'the read path end to end, including what the gateway hop costs' },
  orders: { title: 'Place orders (slow or dead Payment variants)', proves: 'bulkhead/timeout + async payment (Day 7), Kafka + outbox (Day 9), circuit breaker (Day 14)' },
};

// ---- load ------------------------------------------------------------------------------------------
if (!existsSync(RESULTS)) {
  console.error(`no results directory at ${RESULTS}; run evolution/run-checkpoint first`);
  process.exit(1);
}
const runs = readdirSync(RESULTS)
  .filter((f) => f.endsWith('.json') && !f.endsWith('.meta.json'))
  .map((f) => {
    const r = JSON.parse(readFileSync(join(RESULTS, f), 'utf8'));
    const metaPath = join(RESULTS, f.replace(/\.json$/, '.meta.json'));
    const meta = existsSync(metaPath) ? JSON.parse(readFileSync(metaPath, 'utf8')) : {};
    return { ...r, meta };
  });
if (runs.length === 0) {
  console.error(`no results in ${RESULTS}`);
  process.exit(1);
}

// Fold repeats: key = label without the trailing -rN.
const groups = new Map();
for (const r of runs) {
  const key = r.label.replace(/-r\d+$/, '');
  if (!groups.has(key)) groups.set(key, []);
  groups.get(key).push(r);
}
const median = (xs) => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.floor((s.length - 1) / 2)];
};
const rows = [...groups.entries()].map(([key, rs]) => {
  const caps = rs.map((r) => r.capacityRps);
  const m = median(caps);
  const rep = rs.find((r) => r.capacityRps === m); // representative run = the median one
  const day = rep.meta.day || (key.match(/^day-\d+/) || [''])[0];
  return {
    key, day, scenario: rep.scenario, variant: rep.meta.variant || '',
    title: rep.meta.title || (manifest.checkpoints[day] || {}).title || '',
    note: rep.meta.note || '', replicas: rep.meta.replicas || 1,
    capacity: m, capacities: caps, p99AtCapacity: rep.p99AtCapacity, firstFailure: rep.firstFailure,
    steps: rep.steps, runs: rs.length, machine: rep.meta.machine, commit: rep.meta.commit,
  };
});
const dayIdx = (d) => (DAY_ORDER.indexOf(d) < 0 ? 99 : DAY_ORDER.indexOf(d));
rows.sort((a, b) => dayIdx(a.day) - dayIdx(b.day) || a.variant.localeCompare(b.variant));

// Colour follows the entity: within a scenario's chart each checkpoint+variant gets a slot in fixed checkpoint
// order (never cycled; at most 8 series per chart).
const SLOTS = [
  ['#2a78d6', '#3987e5'], ['#eb6834', '#d95926'], ['#1baf7a', '#199e70'], ['#eda100', '#c98500'],
  ['#e87ba4', '#d55181'], ['#008300', '#008300'], ['#6250d6', '#9085e9'], ['#e34948', '#e66767'],
];
const slotOf = new Map();
for (const scn of new Set(rows.map((r) => r.scenario))) {
  rows.filter((r) => r.scenario === scn).forEach((r, i) => slotOf.set(r.key, i));
}

// ---- svg helpers ------------------------------------------------------------------------------------
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const nameOf = (r) => `${r.day}${r.variant ? ` · ${r.variant}` : ''}`;
const fmt = (v, d = 0) => (v === null || v === undefined ? '–' : Number(v).toFixed(d));

function styleBlock(seriesKeys) {
  const slots = [...new Set([0, ...seriesKeys.map((k) => slotOf.get(k))])].filter((i) => i < 8);
  const light = slots.map((i) => `--s${i}:${SLOTS[i][0]};`).join('');
  const dark = slots.map((i) => `--s${i}:${SLOTS[i][1]};`).join('');
  return `<style>
  svg{--surface:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--muted:#898781;--grid:#e1e0d9;--axis:#c3c2b7;${light}}
  @media (prefers-color-scheme: dark){svg{--surface:#1a1a19;--ink:#f0efec;--ink2:#c3c2b7;--muted:#898781;--grid:#2c2c2a;--axis:#383835;${dark}}}
  text{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;fill:var(--ink2);font-size:12px}
  .t{fill:var(--ink);font-size:14px;font-weight:600}.v{fill:var(--ink);font-variant-numeric:tabular-nums}
  .m{fill:var(--muted);font-size:11px}
</style>`;
}

// Horizontal bars: capacity per checkpoint (one series, so no legend box; the title names it).
function capacityChart(scn, rs) {
  const W = 760, left = 230, right = 70, barH = 22, gap = 10, top = 52;
  const H = top + rs.length * (barH + gap) + 34;
  const max = Math.max(1, ...rs.map((r) => r.capacity));
  const x = (v) => left + (v / max) * (W - left - right);
  const ticks = niceTicks(max);
  let s = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(SCENARIOS[scn].title)}: capacity per checkpoint">`;
  s += styleBlock(rs.map((r) => r.key));
  s += `<rect width="${W}" height="${H}" rx="8" style="fill:var(--surface)"/>`;
  s += `<text class="t" x="16" y="24">${esc(SCENARIOS[scn].title)}: capacity (requests/s within SLO)</text>`;
  s += `<text class="m" x="16" y="41">Highest step with p99 under the SLO, errors under 1%, and the server keeping up. Higher is better.</text>`;
  for (const t of ticks) {
    s += `<line x1="${x(t)}" x2="${x(t)}" y1="${top - 4}" y2="${H - 30}" style="stroke:var(--grid)" stroke-width="1"/>`;
    s += `<text class="m" x="${x(t)}" y="${H - 14}" text-anchor="middle">${t}</text>`;
  }
  rs.forEach((r, i) => {
    const y = top + i * (barH + gap);
    const w = Math.max(0, x(r.capacity) - left);
    const fill = 'var(--s0)'; // one series (capacity), one hue
    s += `<text x="${left - 10}" y="${y + barH / 2 + 4}" text-anchor="end">${esc(nameOf(r))}</text>`;
    if (w > 0) {
      const rad = Math.min(4, w); // 4px rounded data end, square at the baseline
      s += `<path d="M${left},${y} h${w - rad} a${rad},${rad} 0 0 1 ${rad},${rad} v${barH - 2 * rad} a${rad},${rad} 0 0 1 -${rad},${rad} h-${w - rad} z" style="fill:${fill}"><title>${esc(nameOf(r))}: ${r.capacity} req/s (p99 ${fmt(r.p99AtCapacity, 1)} ms)</title></path>`;
    }
    s += `<text class="v" x="${left + w + 6}" y="${y + barH / 2 + 4}">${r.capacity}</text>`;
  });
  s += `<line x1="${left}" x2="${left}" y1="${top - 4}" y2="${H - 30}" style="stroke:var(--axis)" stroke-width="1"/>`;
  return s + '</svg>';
}

// Lines: p99 at each load step, one line per checkpoint, log y-axis, SLO reference line.
function curveChart(scn, rs) {
  const series = rs.slice(0, 8); // never a generated 9th hue
  const rates = [...new Set(series.flatMap((r) => r.steps.map((st) => st.targetRps)))].sort((a, b) => a - b);
  const W = 760, left = 64, right = 28, top = 80, bottom = 46;
  const H = 360;
  const vals = series.flatMap((r) => r.steps.map((st) => st.p99).filter((v) => v > 0));
  const lo = Math.pow(10, Math.floor(Math.log10(Math.max(0.5, Math.min(...vals, 300)))));
  const hi = Math.pow(10, Math.ceil(Math.log10(Math.max(...vals, 300))));
  const x = (rate) => left + (rates.indexOf(rate) / Math.max(1, rates.length - 1)) * (W - left - right);
  const y = (v) => top + (1 - (Math.log10(v) - Math.log10(lo)) / (Math.log10(hi) - Math.log10(lo))) * (H - top - bottom);
  let s = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(SCENARIOS[scn].title)}: p99 latency per load step">`;
  s += styleBlock(series.map((r) => r.key));
  s += `<rect width="${W}" height="${H}" rx="8" style="fill:var(--surface)"/>`;
  s += `<text class="t" x="16" y="24">${esc(SCENARIOS[scn].title)}: p99 latency as load rises</text>`;
  // legend row (always present for 2+ series; swatch + ink text)
  let lx = 16;
  series.forEach((r) => {
    const label = nameOf(r);
    s += `<rect x="${lx}" y="36" width="10" height="10" rx="2" style="fill:var(--s${slotOf.get(r.key)})"/>`;
    s += `<text x="${lx + 14}" y="45">${esc(label)}</text>`;
    lx += 14 + label.length * 6.6 + 14;
  });
  for (let d = lo; d <= hi; d *= 10) {
    s += `<line x1="${left}" x2="${W - right}" y1="${y(d)}" y2="${y(d)}" style="stroke:var(--grid)" stroke-width="1"/>`;
    s += `<text class="m" x="${left - 8}" y="${y(d) + 4}" text-anchor="end">${msLabel(d)}</text>`;
  }
  s += `<line x1="${left}" x2="${W - right}" y1="${y(300)}" y2="${y(300)}" style="stroke:var(--ink2)" stroke-width="1" stroke-dasharray="4 4"/>`;
  s += `<text class="m" x="${left + 6}" y="${y(300) - 6}">SLO p99 300 ms</text>`;
  rates.forEach((rate) => { s += `<text class="m" x="${x(rate)}" y="${H - bottom + 18}" text-anchor="middle">${rate}</text>`; });
  s += `<text class="m" x="${(left + W - right) / 2}" y="${H - 10}" text-anchor="middle">target load (requests/s)</text>`;
  series.forEach((r) => {
    const c = `var(--s${slotOf.get(r.key)})`;
    const pts = r.steps.filter((st) => st.p99 > 0);
    if (pts.length > 1) {
      s += `<polyline fill="none" style="stroke:${c}" stroke-width="2" stroke-linejoin="round" points="${pts.map((st) => `${x(st.targetRps)},${y(st.p99)}`).join(' ')}"/>`;
    }
    for (const st of pts) {
      // filled = step passed; hollow = step failed (shape, not just colour)
      const fill = st.ok ? c : 'var(--surface)';
      s += `<circle cx="${x(st.targetRps)}" cy="${y(st.p99)}" r="4" style="fill:${fill};stroke:${st.ok ? 'var(--surface)' : c}" stroke-width="2"><title>${esc(nameOf(r))} @ ${st.targetRps} req/s: p99 ${fmt(st.p99, 1)} ms, errors ${fmt((st.errorRate || 0) * 100, 2)}%, served ${fmt(st.achievedRps, 1)} req/s${st.ok ? '' : ' (FAILED)'}</title></circle>`;
    }
  });
  s += `<text class="m" x="${left}" y="${top - 8}">Filled dot = step within SLO · hollow dot = step failed (latency, errors, or the server fell behind)</text>`;
  return s + '</svg>';
}

function msLabel(ms) {
  if (ms < 1000) return `${ms} ms`;
  return `${ms / 1000} s`;
}

function niceTicks(max) {
  const raw = max / 4;
  const p = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 5, 10].map((m) => m * p).find((v) => v >= raw) || raw;
  const out = [];
  for (let v = 0; v <= max + 1e-9; v += step) out.push(Math.round(v));
  return out;
}

// ---- report ------------------------------------------------------------------------------------------
mkdirSync(CHART_DIR, { recursive: true });
const md = [];
md.push('# Tadka evolution report: what each day of system design bought');
md.push('');
md.push(`Generated by \`node evolution/report.mjs\` from ${runs.length} run(s) in \`${relative(OUT, RESULTS) || '.'}\`. ` +
  'Same workload, same ~200k-order data set and same machine on every checkpoint; only the architecture changes. ' +
  'Method and caveats: [docs/runbooks/evolution-1-to-1m.md](../docs/runbooks/evolution-1-to-1m.md).');
const machines = [...new Set(rows.map((r) => r.machine && `${r.machine.os}, ${r.machine.cores} cores`).filter(Boolean))];
if (machines.length) md.push('', `Machine: ${machines.join(' / ')}${machines.length > 1 ? ' (**mixed machines: do not compare across them**)' : ''}.`);
md.push('', `**Capacity** = the highest load step (requests/s) where p99 < SLO, errors < 1% and the server served at least 95% of the target rate. ` +
  `**Users supported** = capacity ÷ ${RPS_PER_USER} requests/s per active user (one request every ${fmt(1 / RPS_PER_USER, 0)} s during the dinner rush); ` +
  'change it with `--rps-per-user`. That column is a model built on a measured number, not a measurement of users.');

for (const scn of Object.keys(SCENARIOS)) {
  const rs = rows.filter((r) => r.scenario === scn);
  if (rs.length === 0) continue;
  const base = rs[0];
  md.push('', `## ${SCENARIOS[scn].title}`, '', `Proves: ${SCENARIOS[scn].proves}.`, '');
  md.push('| Checkpoint | Variant | What changed | Capacity (req/s) | vs first row | p99 at capacity | First failing step | Users supported | Runs |');
  md.push('|---|---|---|---:|---:|---:|---|---:|---:|');
  for (const r of rs) {
    const vs = base.capacity > 0 ? `${fmt(r.capacity / base.capacity, 1)}×` : (r.capacity > 0 ? 'from 0' : '–');
    const ff = r.firstFailure
      ? `${r.firstFailure.targetRps} req/s: p99 ${fmt(r.firstFailure.p99, 0)} ms, ${fmt((r.firstFailure.errorRate || 0) * 100, 1)}% errors${r.firstFailure.keptUp ? '' : ', fell behind'}`
      : 'none (raise RATES)';
    const runsCell = r.runs > 1 ? `${r.runs} (${r.capacities.join(', ')})` : '1';
    md.push(`| ${r.day} | ${r.variant || '–'} | ${esc(r.title)}${r.note ? ` · ${esc(r.note)}` : ''} | **${r.capacity}** | ${vs} | ${fmt(r.p99AtCapacity, 1)} ms | ${ff} | ${Math.round(r.capacity / RPS_PER_USER).toLocaleString('en-IN')} | ${runsCell} |`);
  }
  writeFileSync(join(CHART_DIR, `${scn}-capacity.svg`), capacityChart(scn, rs));
  md.push('', `![${SCENARIOS[scn].title}: capacity per checkpoint](report/${scn}-capacity.svg)`);
  if (rs.length > 1) {
    writeFileSync(join(CHART_DIR, `${scn}-p99.svg`), curveChart(scn, rs));
    md.push('', `![${SCENARIOS[scn].title}: p99 per load step](report/${scn}-p99.svg)`);
    if (rs.length > 8) md.push('', '_More than 8 series: the p99 chart shows the first 8; all rows are in the table._');
  }
  // per-step detail (the table view behind the curve)
  md.push('', '<details><summary>Per-step numbers</summary>', '');
  md.push('| Checkpoint | Variant | Target req/s | Served req/s | p50 ms | p99 ms | Errors | Within SLO |');
  md.push('|---|---|---:|---:|---:|---:|---:|---|');
  for (const r of rs) {
    for (const st of r.steps) {
      md.push(`| ${r.day} | ${r.variant || '–'} | ${st.targetRps} | ${fmt(st.achievedRps, 1)} | ${fmt(st.p50, 1)} | ${fmt(st.p99, 1)} | ${fmt((st.errorRate || 0) * 100, 2)}% | ${st.ok ? 'yes' : 'no'} |`);
    }
  }
  md.push('', '</details>');
}

writeFileSync(join(OUT, 'report.md'), md.join('\n') + '\n');
console.log(`wrote ${join(OUT, 'report.md')} and ${readdirSync(CHART_DIR).length} chart(s) in ${CHART_DIR}`);
