import { mkdirSync, writeFileSync } from "node:fs";
import { createCanvas, loadImage } from "@napi-rs/canvas";
import { beforeAll, describe, expect, it } from "vitest";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../../locales/main.loader.server.svelte.js";
import * as js from "../../../../../../locales/js.loader.server.js";
import { locales } from "../../../../../../locales/data.js";
import { resolveChartTheme, paint } from "@nocturne/charts/server";
import { everyPrimitive } from "./report-image.fixtures.js";
import { readReportImageRequest, renderReportImage, ReportImageError } from "./render.js";

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

/** The clinical colour each panel must show somewhere in its pixels. */
const expected: Record<string, string> = {
  time_series: "glucose-low",
  range_bands: "glucose-high",
  bar: "glucose-in-range",
  comparison: "glucose-in-range",
  glucose_chart: "insulin-bolus",
};

describe.each(["light", "dark"] as const)("a report panel drawn as a PNG (%s)", (theme) => {
  it.each(panelIds)("draws %s at the requested size, not blank, in the clinical colours", async (panelId) => {
    const request = readReportImageRequest({ result: everyPrimitive, panelId, width: 800, height: 420, theme, devicePixelRatio: 2 });

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
    const colour = paint(palette, `var(--${expected[request.panel.primitive]})`);
    expect(count(image.data, colour)).toBeGreaterThan(20);
  }, 30_000);
});

describe("a report image request", () => {
  const ask = (extra: Record<string, unknown>) => () => readReportImageRequest({ result: everyPrimitive, ...extra });

  it("takes the first chart panel when none is named, at 1200 by 600, light, ratio 1", () => {
    const request = readReportImageRequest({ result: everyPrimitive });
    expect(request.panel.id).toBe(panelIds[0]);
    expect([request.width, request.height, request.theme, request.devicePixelRatio]).toEqual([1200, 600, "light", 1]);
  });

  it.each([
    [{ width: 4000 }, "width: Send a whole number from 320 to 2400."],
    [{ height: 10.5 }, "height: Send a whole number from 200 to 1600."],
    [{ devicePixelRatio: 3 }, "devicePixelRatio: Send 1 or 2."],
    [{ theme: "sepia" }, "theme: Send light or dark."],
    [{ panelId: "nope" }, "panelId: The report has no panel 'nope'."],
    [{ panelId: "figures-6" }, "Panel 'figures-6' is a stat_tiles, which has no image: its figures are in the summary."],
    [{ result: { panels: [] } }, "result: Send a report result as render_report returns it."],
  ])("refuses %j", (extra, message) => {
    expect(ask(extra)).toThrow(ReportImageError);
    expect(ask(extra)).toThrow(message);
  });
});
