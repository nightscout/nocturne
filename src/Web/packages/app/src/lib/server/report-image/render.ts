import { createCanvas, GlobalFonts, Path2D } from "@napi-rs/canvas";
import { renderCapturedChart, type CapturedChart } from "layerchart/server";
import { render } from "svelte/server";
import { readReport, type ReportPanel, type ReportResult } from "@nocturne/charts/core";
import {
  paint,
  resolveChartTheme,
  STATIC_FONT_FAMILY,
  StaticReportPanel,
  staticPrimitive,
  type StaticRender,
} from "@nocturne/charts/server";
import { glucoseChartLabels } from "#lib/components/dashboard/glucose-chart/chart-labels.svelte.js";
import cabin from "../../../../static/fonts/Cabin-VariableFont_wdth,wght.ttf?inline";

/** The limits a request is held to, whoever sends it; the API applies the same before it asks. */
export const REPORT_IMAGE_LIMITS = {
  minWidth: 320,
  maxWidth: 2400,
  minHeight: 200,
  maxHeight: 1600,
  devicePixelRatios: [1, 2],
  /** The most of a request body read: one panel of a report, which the API sends alone. */
  maxBodyBytes: 4 * 1024 * 1024,
} as const;

export interface ReportImageOptions {
  width: number;
  height: number;
  theme: "light" | "dark";
  devicePixelRatio: 1 | 2;
}

export type ReportImageRequest = ReportImageOptions & { report: ReportResult; panel: ReportPanel };

/** Why a request cannot be drawn, in words for the caller. */
export class ReportImageError extends Error {}

/**
 * A request body as a report, one of its chart panels and the size and theme to draw it in. The
 * panel is the one named, else the report's first chart panel.
 */
export function readReportImageRequest(body: unknown): ReportImageRequest {
  if (!isObject(body)) throw new ReportImageError("Send a JSON object.");
  const fields = body;
  const report = readReport(fields.result);
  if (!report) throw new ReportImageError("result: Send a report result as render_report returns it.");

  const width = whole(fields.width ?? 1200, "width", REPORT_IMAGE_LIMITS.minWidth, REPORT_IMAGE_LIMITS.maxWidth);
  const height = whole(fields.height ?? 600, "height", REPORT_IMAGE_LIMITS.minHeight, REPORT_IMAGE_LIMITS.maxHeight);
  const ratio = fields.devicePixelRatio ?? 1;
  if (ratio !== 1 && ratio !== 2) throw new ReportImageError("devicePixelRatio: Send 1 or 2.");
  const theme = fields.theme ?? "light";
  if (theme !== "light" && theme !== "dark") throw new ReportImageError("theme: Send light or dark.");

  const panelId = fields.panelId;
  if (panelId !== undefined && panelId !== null && typeof panelId !== "string")
    throw new ReportImageError("panelId: Send a panel id as text.");
  const panel = panelId
    ? report.panels.find((p) => p.id === panelId)
    : report.panels.find((p) => staticPrimitive(p) && p.status === "ok");
  if (!panel) throw new ReportImageError(panelId ? `panelId: The report has no panel '${panelId}'.` : "The report has no chart panel.");
  if (panel.status !== "ok") throw new ReportImageError(`Panel '${panel.id}' was not drawn: ${panel.sentence ?? panel.summary}`);
  if (!staticPrimitive(panel))
    throw new ReportImageError(`Panel '${panel.id}' is a ${panel.primitive}, which has no image: its figures are in the summary.`);

  return { report, panel, width, height, theme, devicePixelRatio: ratio };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function whole(value: unknown, name: string, min: number, max: number): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max)
    throw new ReportImageError(`${name}: Send a whole number from ${min} to ${max}.`);
  return value;
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

/** The panel as a PNG, drawn by the panel's primitive in its static mode on a canvas of the requested size. */
export function renderReportImage(request: ReportImageRequest): Buffer {
  prepareCanvas();
  const { report, panel, width, height, devicePixelRatio } = request;
  const theme = resolveChartTheme(request.theme);
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
      labels: glucoseChartLabels(),
      noData: "No data in this range.",
    },
  }).body;

  const { chartState, rootNode } = capture;
  if (!chartState || !rootNode) throw new Error(`Panel '${panel.id}' drew nothing to capture.`);
  return Buffer.from(
    renderCapturedChart({ chartState, rootNode }, {
      width,
      height,
      devicePixelRatio,
      background: paint(theme, "var(--background)", "#ffffff"),
      createCanvas: (w, h) => createCanvas(w, h),
    }),
  );
}
