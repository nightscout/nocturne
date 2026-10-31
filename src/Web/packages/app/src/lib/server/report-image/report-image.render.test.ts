import { mkdirSync, writeFileSync } from "node:fs";
import { createCanvas, loadImage } from "@napi-rs/canvas";
import { beforeAll, describe, expect, it } from "vitest";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../../locales/main.loader.server.svelte.js";
import * as js from "../../../../../../locales/js.loader.server.js";
import { locales } from "../../../../../../locales/data.js";
import { resolveChartTheme, paint } from "@nocturne/charts/server";
import { everyPrimitive, stackedBarPanel, timeSeriesPanel } from "./report-image.fixtures.js";
import { readReportImageRequest, renderReportImage, ReportImageError, ReportImageTooLargeError } from "./render.js";

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

const panelIds = everyPrimitive.panels.filter((p) => p.primitive !== "stat_tiles").map((p) => p.id);

/** The API's request for one panel of the fixture report. */
function requestFor(panelId: string, extra: Record<string, unknown> = {}) {
  return { panel: everyPrimitive.panels.find((p) => p.id === panelId), units: "mg/dl", timeZone: "Etc/UTC", ...extra };
}

/** The clinical colour each panel must show somewhere in its pixels. */
const expected: Record<string, string> = {
  time_series: "var(--insulin-bolus)",
  // The limit line, drawn as the live view draws it: the clinical colour at 60% over the page.
  range_bands: "color-mix(in srgb, var(--glucose-high) 60%, var(--background))",
  bar: "var(--glucose-in-range)",
  comparison: "var(--glucose-in-range)",
  glucose_chart: "var(--insulin-bolus)",
};

describe.each(["light", "dark"] as const)("a report panel drawn as a PNG (%s)", (theme) => {
  it.each(panelIds)("draws %s at the requested size, not blank, in the clinical colours", async (panelId) => {
    const request = readReportImageRequest(requestFor(panelId, { width: 800, height: 420, theme, devicePixelRatio: 2 }));

    const png = await runWithLocale("en", async () => renderReportImage(request));

    if (OUT) {
      mkdirSync(OUT, { recursive: true });
      writeFileSync(`${OUT}/${panelId}-${theme}.png`, png);
    }
    const image = await pixels(png);
    expect([image.width, image.height]).toEqual([1600, 840]);
    const palette = resolveChartTheme(theme);
    const background = paint(palette, "var(--background)");
    expect(count(image.data, background, 0)).toBeLessThan(image.width * image.height * 0.97);
    expect(count(image.data, background, 0)).toBeGreaterThan(image.width * image.height * 0.3);
    const colour = paint(palette, expected[request.panel.primitive]);
    expect(count(image.data, colour, 10)).toBeGreaterThan(20);
  }, 30_000);
});

describe("a report image request", () => {
  const ask = (extra: Record<string, unknown>) => () => readReportImageRequest(requestFor(panelIds[0], extra));

  it("draws at 1200 by 600, light, ratio 1 by default, with the tenant's locale and clock carried", () => {
    const request = readReportImageRequest(requestFor(panelIds[0], { locale: "de-DE", hour12: true }));
    expect([request.width, request.height, request.theme, request.devicePixelRatio]).toEqual([1200, 600, "light", 1]);
    expect([request.report.locale, request.report.hour12]).toEqual(["de-DE", true]);
    expect(request.report.panels).toHaveLength(1);
  });

  it("gives one stacked bar a short default height and a comparison a tall one", () => {
    expect(readReportImageRequest(requestFor("ranges-2")).height).toBe(240);
    expect(readReportImageRequest(requestFor("comparison-4")).height).toBe(800);
  });

  it.each([
    [{ width: 4000 }, "width: Send a whole number from 320 to 2400."],
    [{ height: 10.5 }, "height: Send a whole number from 200 to 1600."],
    [{ devicePixelRatio: 3 }, "devicePixelRatio: Send 1 or 2."],
    [{ theme: "sepia" }, "theme: Send light or dark."],
    [{ locale: "not a locale!" }, "locale: Send a language tag such as en-GB."],
    [{ hour12: "yes" }, "hour12: Send true or false."],
    [{ panel: everyPrimitive.panels.find((p) => p.primitive === "stat_tiles") }, "is a stat_tiles, which has no image: its figures are in the summary."],
    [{ panel: { id: "x" } }, "panel: Send one panel and its units as render_report returns them."],
    [{ units: "kelvin" }, "panel: Send one panel and its units as render_report returns them."],
  ])("refuses %j", (extra, message) => {
    expect(ask(extra)).toThrow(ReportImageError);
    expect(ask(extra)).toThrow(message);
  });

  it("refuses a panel with more points than one image draws, as too large", () => {
    const rows = Array.from({ length: 12_001 }, (_, i) => ({ ...timeSeriesPanel.rows[0], time: timeSeriesPanel.rows[0].time + i }));
    const huge = () => readReportImageRequest({ panel: { ...timeSeriesPanel, rows }, units: "mg/dl" });
    expect(huge).toThrow(ReportImageTooLargeError);
    expect(huge).toThrow("has 12004 points, more than the 12000 one image draws");
  });

  it("draws one stacked bar at its default height", async () => {
    const png = await runWithLocale("en", async () => renderReportImage(readReportImageRequest({ panel: stackedBarPanel, units: "mg/dl" })));
    const image = await pixels(png);
    expect([image.width, image.height]).toEqual([1200, 240]);
  }, 30_000);

  it("draws a limit line at the live view's 60% of the clinical colour, not solid", async () => {
    // A flat line in range and no band, so nothing but the high limit is near its colour.
    const flat = { ...timeSeriesPanel, bindings: { x: "time", y: ["glucose"] }, markers: [], rows: timeSeriesPanel.rows.map((r) => ({ ...r, glucose: 120 })) };
    const png = await runWithLocale("en", async () =>
      renderReportImage(readReportImageRequest({ panel: flat, units: "mg/dl", width: 800, height: 420, theme: "light" })));
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
