import { existsSync, readdirSync, readFileSync } from "node:fs";
import { extname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const packages = fileURLToPath(new URL("../../../", import.meta.url));

const CALL = /(?<!\.)\b(?:set|get|has)Context\s*(?:<[^>()]*>)?\(\s*([^,)]*)/g;
const OWNED_STRING = /^nocturne:[a-z0-9-]+:[a-z0-9-]+$/;

function declared(text: string, name: string): string | null {
  const match = new RegExp(String.raw`\b(?:const|let|var)\s+${name}\b[^=\n]*=\s*([^;\n]*)`).exec(text);
  return match ? match[1].trim() : null;
}

function makesIdentity(init: string): boolean {
  return /^(?:Symbol\s*\(|\{\s*\}|new\s+Object\b)/.test(init);
}

/**
 * Dev SSR can evaluate a module twice on a route's first request. A key made in
 * the module (a Symbol, an object, a runed Context, a svelte createContext) is
 * then a different key in each copy, so a reader from one copy misses the setter
 * from the other. A namespaced string is the same key in every copy.
 */
export function contextKeyOffences(text: string): string[] {
  const offences: string[] = [];

  if (/\bimport\s*\{[^}]*\bContext\b[^}]*\}\s*from\s*["']runed["']/.test(text) && /\bnew\s+Context\b/.test(text)) {
    offences.push("runed Context");
  }
  if (/\bimport\s*\{[^}]*\bcreateContext\b[^}]*\}\s*from\s*["']svelte["']/.test(text)) {
    offences.push("svelte createContext");
  }

  for (const call of text.matchAll(CALL)) {
    const arg = call[1].trim();
    if (/^Symbol\s*\(/.test(arg) || /^\{\s*\}$/.test(arg)) {
      offences.push(`inline key ${arg}`);
      continue;
    }
    if (/^["'`]/.test(arg)) {
      if (!OWNED_STRING.test(arg.slice(1, -1))) offences.push(`unnamespaced key ${arg}`);
      continue;
    }
    if (!/^[A-Za-z_$][\w$]*$/.test(arg)) continue;

    const init = declared(text, arg);
    if (init === null) continue;
    if (makesIdentity(init)) offences.push(`key ${arg} made in the module`);
    else if (/^["']/.test(init) && !OWNED_STRING.test(init.slice(1, -1))) offences.push(`unnamespaced key ${arg}`);
  }

  return [...new Set(offences)];
}

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return ["generated", "node_modules"].includes(entry.name) ? [] : sources(path);
    return [".ts", ".svelte"].includes(extname(path)) && !/\.test\./.test(path) ? [path] : [];
  });
}

function packageSources(): string[] {
  return readdirSync(packages, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && existsSync(join(packages, entry.name, "src")))
    .flatMap((entry) => sources(join(packages, entry.name, "src")));
}

describe("context keys", () => {
  it("are namespaced strings in every package", () => {
    const offenders = packageSources().flatMap((path) => {
      const found = contextKeyOffences(readFileSync(path, "utf8"));
      return found.length ? [`${path}: ${found.join(", ")}`] : [];
    });

    expect(offenders).toEqual([]);
  });

  it.each([
    ["a module Symbol", 'const KEY = Symbol("a");\nsetContext(KEY, 1);', "made in the module"],
    ["a lower case Symbol", 'const chartContextKey = Symbol("a");\ngetContext<X>(chartContextKey);', "made in the module"],
    ["an inline Symbol", 'setContext(Symbol("a"), 1);', "inline key"],
    ["an object key", "const KEY = {};\nhasContext(KEY);", "made in the module"],
    ["a runed Context", 'import { Context } from "runed";\nconst c = new Context<X>("x");', "runed Context"],
    ["a svelte createContext", 'import { createContext } from "svelte";', "svelte createContext"],
    ["a bare string", 'const KEY = "auth-store";\nsetContext(KEY, 1);', "unnamespaced"],
    ["an inline bare string", 'getContext("toggleGroup");', "unnamespaced"],
  ])("rejects %s", (_name, text, expected) => {
    expect(contextKeyOffences(text).join()).toContain(expected);
  });

  it.each([
    ["a namespaced string", 'const KEY = "nocturne:app:thing";\nsetContext(KEY, 1);'],
    ["a registry Symbol", 'setContext(Symbol.for("scn-sidebar"), 1);'],
    ["a Symbol with no context call", 'const IN_PROGRESS = Symbol("composing");'],
    ["a canvas context", 'canvas.getContext("2d");'],
  ])("accepts %s", (_name, text) => {
    expect(contextKeyOffences(text)).toEqual([]);
  });
});
