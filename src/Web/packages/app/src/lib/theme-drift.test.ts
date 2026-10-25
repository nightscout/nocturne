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

const chartOnly: Record<string, Record<string, string>> = {
  nocturne: {},
  trio: {},
};

describe.each(["nocturne", "trio"])("glucose-chart %s theme", (theme) => {
  const chart = parseFiles([packages(`glucose-chart/src/themes/${theme}.css`)]);
  const ui = parseFiles([
    packages("ui/src/theme.css"),
    packages(`ui/src/styles/${theme}-theme.css`),
  ]);

  const entries = [...chart].flatMap(([selector, block]) =>
    [...block].map(([name, value]) => ({ selector, name, value }))
  );

  it("declares custom properties", () => {
    expect(entries.length).toBeGreaterThan(0);
  });

  it.each(entries)(
    "$selector $name matches ui",
    ({ selector, name, value }) => {
      const uiValue = ui.get(selector)?.get(name);
      if (uiValue === undefined) {
        expect(
          chartOnly[theme],
          `${name} under "${selector}" is declared by the chart but not by ui`
        ).toHaveProperty([`${selector}|${name}`]);
        return;
      }
      expect(value, `${name} under "${selector}"`).toBe(uiValue);
    }
  );

  it("allowlists only properties the chart declares and ui does not", () => {
    for (const key of Object.keys(chartOnly[theme])) {
      const [selector, name] = key.split("|");
      expect(
        chart.get(selector)?.has(name),
        `${key} is not declared by the chart`
      ).toBe(true);
      expect(
        ui.get(selector)?.has(name),
        `${key} is declared by ui`
      ).toBeFalsy();
    }
  });
});
