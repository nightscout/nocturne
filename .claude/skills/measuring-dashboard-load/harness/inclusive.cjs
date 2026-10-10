// Usage: node inclusive.cjs <profile.cpuprofile> [startMs endMs]
// Inclusive time per script (counted once per sample when any frame from it is on the stack),
// optionally limited to a window of the profile, plus the hottest named functions per script.
const fs = require("fs");
const [file, startArg, endArg] = process.argv.slice(2);
const p = JSON.parse(fs.readFileSync(file, "utf8"));
const byId = new Map(p.nodes.map((n) => [n.id, n]));
const parent = new Map(); for (const n of p.nodes) for (const c of n.children ?? []) parent.set(c, n.id);
const short = (u) => u.replace(/^https?:\/\/[^/]+/, "").replace(/^\/_app\/immutable\//, "");
const incl = {}, fnIncl = {}; let t = p.startTime, total = 0;
const start = startArg ? p.startTime + Number(startArg) * 1000 : -Infinity, end = endArg ? p.startTime + Number(endArg) * 1000 : Infinity;
p.samples.forEach((id, k) => {
  t += p.timeDeltas[k] ?? 0; const dt = (p.timeDeltas[k + 1] ?? 0) / 1000;
  if (t < start || t > end) return;
  const leaf = byId.get(id); if (leaf.callFrame.functionName === "(idle)") return;
  total += dt;
  const seen = new Set(), seenFn = new Set();
  for (let n = id; n !== undefined; n = parent.get(n)) {
    const f = byId.get(n).callFrame; if (!f.url) continue;
    const s = short(f.url);
    if (!seen.has(s)) { seen.add(s); incl[s] = (incl[s] ?? 0) + dt; }
    const fk = `${s} ${f.functionName || "(anon)"}:${f.lineNumber}:${f.columnNumber}`;
    if (!seenFn.has(fk)) { seenFn.add(fk); fnIncl[fk] = (fnIncl[fk] ?? 0) + dt; }
  }
});
console.log(`window busy ${total.toFixed(0)} ms`);
console.log("inclusive by script:"); for (const [k, v] of Object.entries(incl).sort((a, b) => b[1] - a[1]).slice(0, 25)) console.log(v.toFixed(0).padStart(6), k);
console.log("inclusive by function:"); for (const [k, v] of Object.entries(fnIncl).sort((a, b) => b[1] - a[1]).slice(0, 30)) console.log(v.toFixed(0).padStart(6), k);
