import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { CHART_COLORS } from "../enums.js";
import { cssColorToHex, parseCssColor, readCustomProperties } from "./css-color.js";
import { clinicalColorNames, resolveChartTheme, themeColors } from "./resolve-theme.js";
import { paint } from "./static-theme.js";

const MODES = ["light", "dark"] as const;

/** ChartColor values no stylesheet defines: the live chart draws them unset, an image in the muted colour. */
const UNDEFINED_CHART_COLORS = [
  "activity-exercise",
  "activity-illness",
  "activity-travel",
  "tracker-sensor",
  "tracker-cannula",
  "tracker-pod",
  "tracker-reservoir",
  "tracker-battery",
  "tracker-consumable",
  "tracker-appointment",
  "tracker-reminder",
  "tracker-custom",
];

describe("the static chart theme", () => {
  it.each(MODES)("resolves every custom property themes/nocturne.css declares (%s)", (mode) => {
    const colors = themeColors(mode);
    const declared = new Set([...clinicalColorNames().light, ...(mode === "dark" ? clinicalColorNames().dark : [])]);
    const missing = [...declared].filter((name) => !(name in colors));
    expect(missing).toEqual([]);
  });

  it("reads the declarations from the file itself, not from a list of its own", () => {
    const css = readFileSync(new URL("../themes/nocturne.css", import.meta.url), "utf8");
    // Tailwind's `@theme inline` block only maps the colours to utility names.
    const declaring = css.slice(0, css.indexOf("@theme inline"));
    const written = [...declaring.matchAll(/^\s*--([a-z0-9-]+)\s*:/gm)].map((m) => m[1]);
    const { light, dark } = clinicalColorNames();
    expect(new Set([...light, ...dark])).toEqual(new Set(written));
  });

  it("finds every custom property of themes/nocturne.css in a block it reads", () => {
    const css = readFileSync(new URL("../themes/nocturne.css", import.meta.url), "utf8");
    expect(readCustomProperties(css).unread).toEqual([]);
  });

  it("applies every dark override themes/nocturne.css declares", () => {
    const css = readFileSync(new URL("../themes/nocturne.css", import.meta.url), "utf8");
    const dark = resolveChartTheme("dark");
    for (const [name, value] of readCustomProperties(css).dark)
      expect(paint(dark, `var(--${name})`), name).toBe(paint(dark, value));
  });

  it.each(MODES)("resolves every ChartColor the stylesheets define (%s)", (mode) => {
    const colors = themeColors(mode);
    const unresolved = CHART_COLORS.filter((name) => !(name in colors));
    expect(unresolved).toEqual(UNDEFINED_CHART_COLORS);
  });

  it("gives the dark scheme its own values where the stylesheet overrides them", () => {
    const light = themeColors("light");
    const dark = themeColors("dark");
    expect(dark["insulin-scheduled-basal"]).not.toBe(light["insulin-scheduled-basal"]);
    expect(dark.background).not.toBe(light.background);
    expect(dark["glucose-in-range"]).toBe(light["glucose-in-range"]);
  });

  it("paints the clinical colours at the values a browser computes", () => {
    const light = resolveChartTheme("light");
    // oklch(0.6 0.118 184.704), oklch(0.646 0.222 41.116), rgb(30, 150, 252)
    expect(paint(light, "var(--glucose-in-range)")).toBe("#009689");
    expect(paint(light, "var(--glucose-low)")).toBe("#f54900");
    expect(paint(light, "var(--insulin)")).toBe("#1e96fc");
    expect(paint(light, "var(--background)")).toBe("#ffffff");
  });

  it("evaluates the expressions the charts write over theme colours", () => {
    const light = resolveChartTheme("light");
    expect(paint(light, "oklch(from var(--chart-1) l c h / 0.35)")).toMatch(/^#[0-9a-f]{6}59$/);
    expect(paint(light, "color-mix(in oklab, var(--insulin) 22%, transparent)")).toBe("#1e96fc38");
    expect(paint(light, "var(--insulin-additional-basal)")).toBe(paint(light, "var(--basal)"));
    expect(paint(light, "nonsense(1)")).toBe(paint(light, "var(--muted-foreground)"));
  });
});

describe("CSS colour expressions", () => {
  it("reads hex, rgb and oklch with and without alpha", () => {
    expect(cssColorToHex("#abc")).toBe("#aabbcc");
    expect(cssColorToHex("rgba(30, 150, 252, 0.8)")).toBe("#1e96fccc");
    expect(cssColorToHex("oklch(1 0 0)")).toBe("#ffffff");
    expect(cssColorToHex("oklch(0 0 0 / 50%)")).toBe("#00000080");
    expect(cssColorToHex("oklch(62.447% 0.13042 69.605)")).toMatch(/^#[0-9a-f]{6}$/);
  });

  it("mixes in sRGB and with a colour other than transparent", () => {
    expect(cssColorToHex("color-mix(in srgb, white 50%, black)")).toBe("#808080");
    const half = parseCssColor("color-mix(in oklab, white, black)");
    expect(half?.a).toBe(1);
  });

  it("follows var() fallbacks and refuses a cycle", () => {
    const vars = new Map([["a", "var(--b)"], ["b", "var(--a)"]]);
    expect(cssColorToHex("var(--missing, #ff0000)")).toBe("#ff0000");
    expect(cssColorToHex("var(--a)", vars)).toBeNull();
  });

  it("reads :root and .dark declarations inside at-rules, values over several lines joined", () => {
    const { root, dark } = readCustomProperties(`
      :root { --a: oklch(
        0.7 0.12 220
      ); /* note */ --b: #fff; }
      @media not print { .dark { --a: #000; } }
      @theme inline { --color-a: var(--a); }`);
    expect(root.get("a")).toBe("oklch(0.7 0.12 220)");
    expect(root.get("b")).toBe("#fff");
    expect(dark.get("a")).toBe("#000");
    expect(root.has("color-a")).toBe(false);
  });

  it("reads :root under a dark colour-scheme query, and dark selector lists, as the dark scheme", () => {
    const { root, dark, unread } = readCustomProperties(`
      :root { --a: #fff; --b: #eee; }
      @media (prefers-color-scheme: dark) { :root { --a: #000; } }
      :root.dark { --b: #111; }
      .dark, [data-theme="night"] { --c: #222; }
      @media print { :root { --d: #333; } }`);
    expect(root.get("a")).toBe("#fff");
    expect(dark.get("a")).toBe("#000");
    expect(dark.get("b")).toBe("#111");
    expect(dark.get("c")).toBe("#222");
    expect(root.get("d")).toBe("#333");
    expect(unread).toEqual([]);
  });

  it("names a custom property declared in a block it does not read", () => {
    const { unread } = readCustomProperties(`
      .chart { --lane: red; }
      :root { --a: #fff; .nested { --b: #000; } --c: #111; }
      @theme inline { --color-a: var(--a); }`);
    expect(unread).toEqual([{ selector: ".chart", name: "lane" }, { selector: ".nested", name: "b" }]);
  });

  it("keeps a block's declarations that follow a nested block", () => {
    expect(readCustomProperties(":root { --a: #fff; .x { --b: #000; } --c: #111; }").root.get("c")).toBe("#111");
  });

  it("scales a colour's alpha for a mark that ignores opacity", () => {
    expect(paint(resolveChartTheme("light"), "#ff0000", undefined, 0.5)).toBe("#ff000080");
  });
});
