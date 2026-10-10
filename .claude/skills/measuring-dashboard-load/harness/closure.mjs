// Usage: node closure.mjs <manifest.json> [route-dir]  e.g. src/Web/packages/app/.svelte-kit/output/client/.vite/manifest.json "(authenticated)"
// Static (non-dynamic-import) chunk closure of a route's load path, from the Vite client manifest:
// the client entry, the app shell, the root layout, every layout down route-dir under src/routes, and its +page.
// route-dir is relative to src/routes ("" for the root page, default "(authenticated)", the dashboard).
// Prints the chunk count and byte total, and whether zod or tailwind-merge landed in the closure.
// Expects the manifest at <app>/.svelte-kit/output/client/.vite/manifest.json.
import fs from "node:fs";
import path from "node:path";

const manifestPath = path.resolve(process.argv[2] ?? "");
if (!process.argv[2] || !fs.existsSync(manifestPath)) {
  console.error("usage: node closure.mjs <manifest.json> [route-dir]");
  process.exit(2);
}
const routeDir = (process.argv[3] ?? "(authenticated)").replace(/^\/+|\/+$/g, "");
const root = path.dirname(path.dirname(manifestPath));
const app = path.resolve(root, "../../..");
const gen = path.join(app, ".svelte-kit/generated/client-optimized/nodes");
const m = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const keys = Object.keys(m);

const nodeFor = (route, kind) => {
  const needle = `src/routes/${route ? route + "/" : ""}+${kind}.svelte"`;
  const f = fs.readdirSync(gen).find((f) => fs.readFileSync(path.join(gen, f), "utf8").includes(needle));
  if (!f) throw new Error("no generated node for " + needle);
  return `.svelte-kit/generated/client-optimized/nodes/${f}`;
};

const segments = routeDir ? routeDir.split("/") : [];
const layouts = ["", ...segments.map((_, i) => segments.slice(0, i + 1).join("/"))];
const seeds = [
  keys.find((k) => /runtime\/client\/entry\.js$/.test(k)),
  ".svelte-kit/generated/client-optimized/app.js",
  ...layouts.map((r) => nodeFor(r, "layout")),
  nodeFor(routeDir, "page"),
];

const seen = new Set();
const walk = (k) => {
  if (!m[k]) throw new Error("missing from manifest: " + k);
  if (seen.has(k)) return;
  seen.add(k);
  for (const i of m[k].imports ?? []) walk(i);
};
seeds.forEach(walk);

const files = new Set([...seen].map((k) => m[k].file));
let bytes = 0;
const zod = [], twm = [];
for (const f of files) {
  const s = fs.readFileSync(path.join(root, f), "utf8");
  const n = Buffer.byteLength(s);
  bytes += n;
  if (s.includes("ZodError") || s.includes("$ZodType")) zod.push(`${f} ${n}`);
  if (s.includes("classGroupId")) twm.push(`${f} ${n}`);
}
console.log("seeds", seeds.map((s) => s.replace(/^.*node_modules\//, "")));
console.log("chunks", files.size, "bytes", bytes);
console.log("zod in closure", zod);
console.log("tailwind-merge in closure", twm);
