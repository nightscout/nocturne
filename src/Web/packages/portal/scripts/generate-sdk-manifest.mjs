// Derives the list of supported SDKs from the OpenAPI-generator configs under
// sdk/<lang>/config.yaml — the same files the sdk-publish.yml workflow uses to
// build and publish each SDK. The docs SDK page consumes the emitted manifest,
// so the page's language list can never drift from what we actually publish.
//
// Output (gitignored, regenerated on every dev/build/check): a flat list of
// { dir, generator, package }. Presentation (registry URLs, install commands)
// lives alongside in sdks.ts, keyed by dir. Run via the dev/build/check scripts.

import { readFileSync, readdirSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

/** Walk up from the script dir until we find the repo's sdk/ directory. */
function findSdkDir() {
  let cur = here;
  for (let i = 0; i < 8; i++) {
    const candidate = join(cur, "sdk");
    if (existsSync(join(candidate, "filter-v4-spec.js"))) return candidate;
    const parent = dirname(cur);
    if (parent === cur) break;
    cur = parent;
  }
  return null;
}

/**
 * Minimal reader for our flat, hand-maintained config.yaml files. We only need
 * the top-level generatorName and a handful of additionalProperties keys, so a
 * line-wise `key: value` scan is enough — no YAML dependency.
 */
function readConfig(text) {
  const out = {};
  for (const raw of text.split(/\r?\n/)) {
    const m = raw.match(/^\s*([A-Za-z0-9_]+):\s*(.+?)\s*$/);
    if (!m) continue;
    const [, key, value] = m;
    out[key] = value.replace(/^["']|["']$/g, "");
  }
  return out;
}

/** The canonical published identifier differs by generator. */
function packageId(generator, c) {
  switch (generator) {
    case "typescript-fetch":
      return c.npmName;
    case "python":
      return c.projectName; // pip/PyPI name (import name is packageName)
    case "swift6":
      return c.projectName; // SPM product name
    case "java":
    case "kotlin":
      return c.groupId && c.artifactId ? `${c.groupId}:${c.artifactId}` : c.artifactId;
    default:
      return c.packageName ?? c.npmName ?? c.projectName ?? c.artifactId;
  }
}

const sdkDir = findSdkDir();
const entries = [];

if (!sdkDir) {
  console.warn("[sdk-manifest] sdk/ directory not found; writing empty manifest");
} else {
  for (const dir of readdirSync(sdkDir, { withFileTypes: true })) {
    if (!dir.isDirectory()) continue;
    const configPath = join(sdkDir, dir.name, "config.yaml");
    if (!existsSync(configPath)) continue;
    const c = readConfig(readFileSync(configPath, "utf8"));
    if (!c.generatorName) continue;
    entries.push({
      dir: dir.name,
      generator: c.generatorName,
      package: packageId(c.generatorName, c) ?? dir.name,
    });
  }
  entries.sort((a, b) => a.dir.localeCompare(b.dir));
}

const outDir = resolve(here, "../src/lib/sdk/generated");
mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, "manifest.json"), JSON.stringify(entries, null, 2) + "\n");
console.log(`[sdk-manifest] wrote ${entries.length} SDK(s) to src/lib/sdk/generated/manifest.json`);
