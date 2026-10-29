import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const packages = (path: string) =>
  fileURLToPath(new URL(`../../../${path}`, import.meta.url));

type Declarations = Map<string, Map<string, string>>;

function parseCustomProperties(css: string): Declarations {
  const declarations: Declarations = new Map();
  const selectors: string[] = [];
  let buffer = "";

  const flush = () => {
    const match = buffer.trim().match(/^(--[\w-]+)\s*:\s*([\s\S]+)$/);
    buffer = "";
    if (!match) return;
    const selector = selectors.join(" > ");
    const block = declarations.get(selector) ?? new Map<string, string>();
    block.set(match[1], match[2].replace(/\s+/g, " ").trim());
    declarations.set(selector, block);
  };

  for (const char of css.replace(/\/\*[\s\S]*?\*\//g, "")) {
    if (char === "{") {
      selectors.push(buffer.trim().replace(/\s+/g, " "));
      buffer = "";
    } else if (char === "}") {
      flush();
      selectors.pop();
    } else if (char === ";") {
      flush();
    } else {
      buffer += char;
    }
  }
  return declarations;
}

function parseFiles(paths: string[]): Declarations {
  const merged: Declarations = new Map();
  for (const path of paths) {
    for (const [selector, block] of parseCustomProperties(
      readFileSync(path, "utf8")
    )) {
      const target = merged.get(selector) ?? new Map<string, string>();
      for (const [name, value] of block) target.set(name, value);
      merged.set(selector, target);
    }
  }
  return merged;
}

/**
 * The clinical colours have one definition, in @nocturne/charts. ui's default theme keeps
 * the rest; its other themes restyle both under more specific selectors.
 */
describe("the chart theme", () => {
  const chart = parseFiles([packages("charts/src/themes/nocturne.css")]);
  const ui = parseFiles([
    packages("ui/src/theme.css"),
    packages("ui/src/styles/nocturne-theme.css"),
  ]);

  const entries = [...chart].flatMap(([selector, block]) =>
    [...block].map(([name, value]) => ({ selector, name, value }))
  );

  it("declares custom properties", () => {
    expect(entries.length).toBeGreaterThan(0);
  });

  it.each(entries)("$selector $name is declared only by the chart", ({ selector, name }) => {
    expect(ui.get(selector)?.has(name), `${name} under "${selector}"`).toBeFalsy();
  });
});
