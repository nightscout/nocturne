// Usage: node bundle-analyse.cjs <bundle-report.json> <client-out-dir> <entry chunk files...>
// Static import closure of the given entry chunks, broken down by package / source area, with
// gzip sizes per chunk, plus the dynamic chunks those entries can pull in.
const fs = require("fs"), path = require("path"), zlib = require("zlib");
const [report, outDir, ...entryFiles] = process.argv.slice(2);
const chunks = JSON.parse(fs.readFileSync(report, "utf8"));
const byFile = new Map(chunks.map((c) => [c.fileName, c]));
const closure = new Set();
const visit = (f) => { if (closure.has(f)) return; closure.add(f); for (const i of byFile.get(f)?.imports ?? []) visit(i); };
for (const e of entryFiles) visit(e);
const dyn = new Set();
for (const f of closure) for (const d of byFile.get(f)?.dynamicImports ?? []) if (!closure.has(d)) dyn.add(d);

const gz = (f) => { const p = path.join(outDir, f); return fs.existsSync(p) ? zlib.gzipSync(fs.readFileSync(p)).length : 0; };
let raw = 0, gzip = 0;
const area = {};
const pkgOf = (id) => {
  const nm = id.lastIndexOf("node_modules/");
  if (nm >= 0) { const rest = id.slice(nm + 13).split("/"); return "npm:" + (rest[0].startsWith("@") ? rest[0] + "/" + rest[1] : rest[0]); }
  const m = id.match(/src\/(lib\/[^/]+(?:\/[^/]+)?|routes\/[^/]+)/); if (m) return "app:" + m[1];
  const p = id.match(/packages\/([^/]+)\//); if (p) return "pkg:" + p[1];
  return "other:" + id.split("/").slice(-2).join("/");
};
const moduleSizes = [];
for (const f of closure) {
  const c = byFile.get(f); if (!c) continue;
  raw += c.size; gzip += gz(f);
  for (const [rawId, len] of Object.entries(c.modules)) {
    const id = rawId.split(String.fromCharCode(92)).join("/");
    const k = pkgOf(id);
    area[k] = (area[k] ?? 0) + len;
    moduleSizes.push([len, id.replace(/.*node_modules\//, "nm/").replace(/.*\/src\//, "src/")]);
  }
}
console.log(`static closure: ${closure.size} chunks, ${(raw / 1024).toFixed(0)} KB raw (minified), ${(gzip / 1024).toFixed(0)} KB gzip`);
console.log(`dynamic chunks reachable from it: ${dyn.size}, ${([...dyn].reduce((a, f) => a + (byFile.get(f)?.size ?? 0), 0) / 1024).toFixed(0)} KB raw`);
console.log("\nby area (rendered KB, top 30):");
for (const [k, v] of Object.entries(area).sort((a, b) => b[1] - a[1]).slice(0, 30)) console.log(String((v / 1024).toFixed(1)).padStart(8), k);
console.log("\nlargest modules (rendered KB, top 25):");
for (const [len, id] of moduleSizes.sort((a, b) => b[0] - a[0]).slice(0, 25)) console.log(String((len / 1024).toFixed(1)).padStart(8), id.slice(0, 120));
