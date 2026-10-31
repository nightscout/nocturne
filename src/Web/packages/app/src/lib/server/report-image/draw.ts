import { createCanvas, GlobalFonts, Path2D } from "@napi-rs/canvas";
import { renderCapturedChart, type CapturedChart } from "layerchart/server";
import { render } from "svelte/server";
import { deserializeChartLabels, type ReportPanel, type ReportResult, type SerializedChartLabels } from "@nocturne/charts/core";
import {
  paint,
  resolveChartTheme,
  STATIC_FONT_FAMILY,
  StaticReportPanel,
  type StaticRender,
} from "@nocturne/charts/server";
import cabin from "../../../../static/fonts/Cabin-VariableFont_wdth,wght.ttf?inline";

/**
 * Everything one image needs, as plain data so it can cross to a worker thread: the panel and the
 * parts of its report the drawing reads, the size, theme and ratio, and the words in the reader's
 * language (`moreRows` holds "{0}" where the count goes).
 */
export interface DrawJob {
  report: ReportResult;
  panel: ReportPanel;
  width: number;
  height: number;
  theme: "light" | "dark";
  devicePixelRatio: 1 | 2;
  labels: SerializedChartLabels;
  noData: string;
  moreRows: string;
}

let canvasReady = false;

/** Path2D for layerchart's canvas marks, and the app's Cabin, so text measures the same on every host. */
function prepareCanvas() {
  if (canvasReady) return;
  if (!("Path2D" in globalThis)) Object.assign(globalThis, { Path2D });
  const base64 = String(cabin).slice(String(cabin).indexOf(",") + 1);
  GlobalFonts.register(Buffer.from(base64, "base64"), STATIC_FONT_FAMILY);
  canvasReady = true;
}

/** The panel as a PNG, drawn by its primitive in its static mode on a canvas of the job's size. */
export function drawPanel(job: DrawJob): Uint8Array {
  prepareCanvas();
  const { report, panel, width, height, devicePixelRatio } = job;
  const theme = resolveChartTheme(job.theme);
  const capture: Partial<CapturedChart> = {};
  const scratch = createCanvas(1, 1).getContext("2d");
  const still: StaticRender = {
    theme,
    width,
    height,
    capture,
    gradient: (y0, y1, stops) => {
      const gradient = scratch.createLinearGradient(0, y0, 0, y1);
      for (const [offset, color] of stops) gradient.addColorStop(Math.min(1, Math.max(0, offset)), color);
      return gradient;
    },
  };

  void render(StaticReportPanel, {
    props: {
      report,
      panel,
      still,
      format: { locale: "en-GB", hour12: false },
      labels: deserializeChartLabels(job.labels),
      noData: job.noData,
      moreRows: (count: number) => job.moreRows.replaceAll("{0}", String(count)),
    },
  }).body;

  const { chartState, rootNode } = capture;
  if (!chartState || !rootNode) throw new Error(`Panel '${panel.id}' drew nothing to capture.`);
  const png = renderCapturedChart(
    { chartState, rootNode },
    {
      width,
      height,
      devicePixelRatio,
      background: paint(theme, "var(--background)", "#ffffff"),
      createCanvas: (w, h) => createCanvas(w, h),
    },
  );
  return new Uint8Array(png.buffer, png.byteOffset, png.byteLength);
}
