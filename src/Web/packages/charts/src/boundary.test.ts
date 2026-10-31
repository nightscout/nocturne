import { describe, expect, it } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * The package draws from props alone. ESLint's no-restricted-imports enforces this per import;
 * this reads every source's specifiers, so a rule that stops matching fails here too.
 */
const root = fileURLToPath(new URL(".", import.meta.url));

const FORBIDDEN: { pattern: RegExp; reason: string }[] = [
  { pattern: /^\$app(\/|$)/, reason: "SvelteKit runtime" },
  { pattern: /^\$lib(\/|$)/, reason: "SvelteKit alias" },
  { pattern: /^#(lib|routes)\//, reason: "app module" },
  { pattern: /^@sveltejs\/kit(\/|$)/, reason: "SvelteKit runtime" },
  { pattern: /^@nocturne\/app(\/|$)/, reason: "app module" },
  { pattern: /^(@wuchale\/|wuchale(\/|$))/, reason: "translation runtime" },
  { pattern: /\.remote(\.(js|ts))?$/, reason: "remote function" },
  { pattern: /^svelte\/store$/, reason: "store" },
  { pattern: /(^|\/)stores\/|-store(\.|$)/, reason: "app store" },
];

/**
 * The only @nocturne/ui modules allowed: plain TS with no Svelte and nothing of the app, and the
 * stylesheets the static theme reads as text.
 */
const UI_ALLOWED = new Set([
  "@nocturne/ui/glucose",
  "@nocturne/ui/utils",
  "@nocturne/ui/keys",
  "@nocturne/ui/theme.css?raw",
  "@nocturne/ui/styles/nocturne-theme.css?raw",
]);

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) return sources(full);
    return /\.(ts|svelte)$/.test(entry.name) && !entry.name.endsWith(".test.ts") ? [full] : [];
  });
}

const SPECIFIER = /(?:import|export)\s[^"']*?from\s*["']([^"']+)["']|import\s*\(\s*["']([^"']+)["']\s*\)|import\s+["']([^"']+)["']/g;

describe("@nocturne/charts imports", () => {
  const files = sources(root);

  it("reads the package's sources", () => {
    expect(files.length).toBeGreaterThan(50);
  });

  it("reach nothing of the app's", () => {
    const found: string[] = [];
    for (const file of files) {
      for (const match of readFileSync(file, "utf8").matchAll(SPECIFIER)) {
        const specifier = match[1] ?? match[2] ?? match[3];
        if (/^@nocturne\/ui(\/|$)/.test(specifier) && !UI_ALLOWED.has(specifier)) {
          found.push(`${relative(root, file)}: ${specifier} (only ${[...UI_ALLOWED].join(", ")} of @nocturne/ui)`);
        }
        const hit = FORBIDDEN.find((f) => f.pattern.test(specifier));
        if (hit) found.push(`${relative(root, file)}: ${specifier} (${hit.reason})`);
        if (specifier.startsWith(".") && relative(root, resolve(dirname(file), specifier)).startsWith("..")) {
          found.push(`${relative(root, file)}: ${specifier} (outside the package)`);
        }
      }
    }
    expect(found).toEqual([]);
  });
});
