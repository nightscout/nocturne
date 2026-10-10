// Usage: node summarise.cjs <variantA> <variantB>. Pools both rounds per variant (median of all raw runs).
const fs = require("fs");
const path = require("path");
const dir = path.join(__dirname, "results");
const median = (xs) => { const s = xs.filter((x) => typeof x === "number" && x >= 0).sort((a, b) => a - b); return s.length ? s[Math.floor(s.length / 2)] : null; };
const load = (f) => { try { return JSON.parse(fs.readFileSync(path.join(dir, f), "utf8")); } catch { return null; } };

const variants = process.argv.slice(2, 4).length === 2 ? process.argv.slice(2, 4) : ["main", "branch"];
const [A, B] = variants;
const perf = {}, bench = {};
for (const v of variants) {
  perf[v] = {}; bench[v] = {};
  for (const r of [1, 2, 3, 4, 5]) {
    const p = load(`perf-${v}-r${r}.json`);
    if (p) for (const [s, d] of Object.entries(p.scenarios)) (perf[v][s] ??= []).push(...d.raw);
    const b = load(`bench-${v}-r${r}.json`);
    if (b) for (const [k, d] of Object.entries(b)) if (d && typeof d === "object") (bench[v][k] ??= []).push(d.median_ms);
  }
}

const metrics = ["clsMilli", "fcpWallMs", "dashLcpWallMs", "plotWallMs", "lcpWallMs", "ttfbWallMs", "loadMs", "settledMs", "redirects", "blockingCss", "blockingCssBytes", "jsChunks", "dataRequests"];
const row = (name, a, b) => {
  const d = a != null && b != null ? b - a : null;
  const pct = d != null && a ? ` (${d > 0 ? "+" : ""}${Math.round((d / a) * 100)}%)` : "";
  return `| ${name} | ${a ?? "-"} | ${b ?? "-"} | ${d != null ? (d > 0 ? "+" : "") + d + pct : "-"} |`;
};
const lines = [];
for (const s of Object.keys(perf[A] ?? {})) {
  lines.push(`\n### ${s} (n=${perf[A][s].length}/${perf[B][s]?.length ?? 0})\n`, `| metric | ${A} | ${B} | delta |`, "|---|---|---|---|");
  for (const m of metrics) lines.push(row(m, median(perf[A][s].map((r) => r[m])), median((perf[B][s] ?? []).map((r) => r[m]))));
}
lines.push("\n### server timings, no added latency (median of round medians)\n", `| request | ${A} | ${B} | delta |`, "|---|---|---|---|");
for (const k of Object.keys(bench[A] ?? {})) lines.push(row(k, median(bench[A][k]), median(bench[B][k] ?? [])));
console.log(lines.join("\n"));
