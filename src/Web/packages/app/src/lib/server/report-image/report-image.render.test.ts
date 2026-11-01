import { mkdirSync, writeFileSync } from "node:fs";
import { createCanvas, loadImage } from "@napi-rs/canvas";
import { beforeAll, describe, expect, it } from "vitest";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../../locales/main.loader.server.svelte.js";
import * as js from "../../../../../../locales/js.loader.server.js";
import { locales } from "../../../../../../locales/data.js";
import { readReport } from "@nocturne/charts/core";
import { resolveChartTheme, paint } from "@nocturne/charts/server";
import { everyPanel, glucoseChartPanel, stackedBarPanel } from "./report-image.fixtures.js";
import { readOptions, renderReportImage, ReportImageError, ReportImageTooLargeError } from "./render.js";

/** Set to a directory to keep the images the tests draw, to look at them. */
const OUT = process.env.REPORT_IMAGE_OUT;

async function pixels(png: Buffer) {
  const image = await loadImage(png);
  const canvas = createCanvas(image.width, image.height);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(image, 0, 0);
  return { width: image.width, height: image.height, data: ctx.getImageData(0, 0, image.width, image.height).data };
}

const rgb = (hex: string) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));

/** How many pixels lie within `tolerance` of `hex` on every channel. */
function count(data: Uint8ClampedArray, hex: string, tolerance = 6): number {
  const [r, g, b] = rgb(hex);
  let n = 0;
  for (let i = 0; i < data.length; i += 4)
    if (Math.abs(data[i] - r) <= tolerance && Math.abs(data[i + 1] - g) <= tolerance && Math.abs(data[i + 2] - b) <= tolerance) n++;
  return n;
}

beforeAll(async () => {
  await loadLocales(main.key, main.loadCount, main.loadCatalog, locales);
  await loadLocales(js.key, js.loadCount, js.loadCatalog, locales);
});

const panelIds = everyPanel.panels.filter((p) => p.kind !== "stat_tiles").map((p) => p.id);

/** One panel of a report, with the size, theme and ratio in `options`, as the download asks for it. */
function requestOf(panel: unknown, options: Record<string, unknown> = {}, report: { locale?: string; hour12?: boolean } = {}) {
  const read = readReport({
    spec: { version: 1, title: null },
    units: "mg/dl",
    timeZone: "Etc/UTC",
    glucoseColorMode: "continuous",
    locale: report.locale ?? null,
    hour12: report.hour12 ?? null,
    summary: "",
    warnings: [],
    panels: [panel],
  });
  if (!read) throw new Error("The fixture panel is not a report panel.");
  return readOptions(read, read.panels[0], options);
}

function requestFor(panelId: string, options: Record<string, unknown> = {}) {
  return requestOf(everyPanel.panels.find((p) => p.id === panelId), options);
}

/** The colour each panel must show somewhere in its pixels, by the panel's id before its place is added. */
const expected: Record<string, string> = {
  // A bolus glyph, the COB line and the automatic mode's lane.
  glucose: "var(--insulin-bolus)",
  // The limit line, drawn as the live view draws it: the clinical colour at 60% over the page.
  profile: "color-mix(in srgb, var(--glucose-high) 60%, var(--background))",
  ranges: "var(--glucose-in-range)",
  days: "var(--glucose-in-range)",
  comparison: "var(--glucose-in-range)",
  // The fit line through the points.
  scatter: "var(--muted-foreground)",
  lag: "var(--primary)",
  week: "var(--glucose-high)",
  // The weekend's middle line, in the series hue the API gave its layer.
  weekdays: "var(--chart-1)",
};

describe.each(["light", "dark"] as const)("a report panel drawn as a PNG (%s)", (theme) => {
  it.each(panelIds)("draws %s at the requested size, not blank, in the clinical colours", async (panelId) => {
    const request = requestFor(panelId, { width: 800, height: 420, theme, devicePixelRatio: 2 });

    const png = await runWithLocale("en", async () => renderReportImage(request));

    if (OUT) {
      mkdirSync(OUT, { recursive: true });
      writeFileSync(`${OUT}/${panelId}-${theme}.png`, png);
    }
    const image = await pixels(png);
    expect([image.width, image.height]).toEqual([1600, 840]);
    const palette = resolveChartTheme(theme);
    const background = paint(palette, "var(--background)");
    expect(count(image.data, background, 0)).toBeLessThan(image.width * image.height * 0.99);
    expect(count(image.data, background, 0)).toBeGreaterThan(image.width * image.height * 0.3);
    const colour = paint(palette, expected[request.panel.id.replace(/-\d+$/, "")]);
    expect(count(image.data, colour, 10)).toBeGreaterThan(20);
  }, 30_000);
});

describe("a report image request", () => {
  const ask = (extra: Record<string, unknown>) => () => requestFor(panelIds[0], extra);

  it("draws at 1200 by 600, light, ratio 1 by default, with the tenant's locale and clock carried", () => {
    const request = requestOf(everyPanel.panels.find((p) => p.id === "lag-6"), {}, { locale: "de-DE", hour12: true });
    expect([request.width, request.height, request.theme, request.devicePixelRatio]).toEqual([1200, 600, "light", 1]);
    expect([request.report.locale, request.report.hour12]).toEqual(["de-DE", true]);
    expect(request.report.panels).toHaveLength(1);
  });

  it("gives one stacked bar a short default height, a comparison a tall one and a chart room for its tracks", () => {
    expect(requestFor("ranges-2").height).toBe(240);
    expect(requestFor("comparison-4").height).toBe(800);
    expect(requestFor("glucose-0").height).toBe(1116);
    expect(requestFor("lag-6").height).toBe(600);
  });

  it.each([
    [{ width: 4000 }, "width: Send a whole number from 320 to 2400."],
    [{ height: 10.5 }, "height: Send a whole number from 200 to 1600."],
    [{ devicePixelRatio: 3 }, "devicePixelRatio: Send 1 or 2."],
    [{ theme: "sepia" }, "theme: Send light or dark."],
  ])("refuses %j", (extra, message) => {
    expect(ask(extra)).toThrow(ReportImageError);
    expect(ask(extra)).toThrow(message);
  });

  it("refuses a panel with no image", () => {
    const tiles = () => requestOf(everyPanel.panels.find((p) => p.kind === "stat_tiles"));
    expect(tiles).toThrow(ReportImageError);
    expect(tiles).toThrow("is a stat_tiles panel, which has no image: its figures are in the summary.");
  });

  it("refuses a panel with more points than one image draws, as too large", () => {
    const glucose = glucoseChartPanel.tracks[1].layers[1];
    const rows = Array.from({ length: 12_001 }, (_, i) => ({ ...glucose.rows[0], time: Number(glucose.rows[0].time) + i }));
    const tracks = [{ ...glucoseChartPanel.tracks[1], layers: [{ ...glucose, rows }] }];
    const huge = () => requestOf({ ...glucoseChartPanel, tracks });
    expect(huge).toThrow(ReportImageTooLargeError);
    expect(huge).toThrow("has 12001 points, more than the 12000 one image draws");
  });

  it("draws one stacked bar at its default height", async () => {
    const png = await runWithLocale("en", async () => renderReportImage(requestOf(stackedBarPanel)));
    const image = await pixels(png);
    expect([image.width, image.height]).toEqual([1200, 240]);
  }, 30_000);

  it("draws a limit line at the live view's 60% of the clinical colour, not solid", async () => {
    // A flat line in range and no band, so nothing but the high limit is near its colour.
    const glucose = glucoseChartPanel.tracks[1].layers[1];
    const flat = { ...glucoseChartPanel, tracks: [{ ...glucoseChartPanel.tracks[1], layers: [{ ...glucose, rows: glucose.rows.map((r) => ({ ...r, glucose: 120 })) }] }] };
    const png = await runWithLocale("en", async () =>
      renderReportImage(requestOf(flat, { width: 800, height: 420, theme: "light" })));
    const image = await pixels(png);
    const palette = resolveChartTheme("light");
    // Antialiasing spreads the one-pixel line a little: at the live view's 60% it shows as about a
    // 50% tint; drawn solid, as before strokeOpacity was baked into the colour, at about 90%.
    const solid = paint(palette, "color-mix(in srgb, var(--glucose-high) 90%, var(--background))");
    const faded = paint(palette, "color-mix(in srgb, var(--glucose-high) 50%, var(--background))");
    expect(count(image.data, solid, 12)).toBeLessThanOrEqual(2);
    expect(count(image.data, faded, 12)).toBeGreaterThan(20);
  }, 30_000);
});

describe("a chart panel drawn as a PNG paints every track", () => {
  it("draws the spans lane, the right side's COB line and the area of basal", async () => {
    const png = await runWithLocale("en", async () => renderReportImage(requestOf(glucoseChartPanel, { width: 1000, theme: "light" })));
    const image = await pixels(png);
    const palette = resolveChartTheme("light");
    expect(count(image.data, paint(palette, "var(--carbs)"), 10)).toBeGreaterThan(20);
    expect(count(image.data, paint(palette, "color-mix(in srgb, var(--pump-mode-automatic) 55%, var(--background))"), 14)).toBeGreaterThan(200);
  }, 30_000);
});
