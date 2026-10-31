import { existsSync } from "node:fs";
import { availableParallelism } from "node:os";
import { resolve } from "node:path";
import { readReport, type ReportPanel, type ReportResult } from "@nocturne/charts/core";
import { staticDefaultHeight, staticPrimitive } from "@nocturne/charts/server";
import { drawPanel, type DrawJob } from "./draw.js";
import { RenderPool } from "./pool.js";
import { reportImageWords } from "./report-image-words.svelte.js";

/** The limits a request is held to, whoever sends it; the API applies the same before it asks. */
export const REPORT_IMAGE_LIMITS = {
  minWidth: 320,
  maxWidth: 2400,
  minHeight: 200,
  maxHeight: 1600,
  /** The most of a request body read: one panel, which the API sends alone. */
  maxBodyBytes: 4 * 1024 * 1024,
  /**
   * The most rows, trace points and chart points one panel may hold. The API sends at most 2000 rows
   * a series; past this a single render takes seconds and hundreds of megabytes.
   */
  maxPoints: 12_000,
} as const;

/** Why a request cannot be drawn, in words for the caller. */
export class ReportImageError extends Error {}

/** A panel with more points than one image draws (`REPORT_IMAGE_LIMITS.maxPoints`); the route answers 413. */
export class ReportImageTooLargeError extends ReportImageError {}

export interface ReportImageRequest {
  report: ReportResult;
  panel: ReportPanel;
  width: number;
  height: number;
  theme: "light" | "dark";
  devicePixelRatio: 1 | 2;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function whole(value: unknown, name: string, min: number, max: number): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max)
    throw new ReportImageError(`${name}: Send a whole number from ${min} to ${max}.`);
  return value;
}

/** A BCP-47 tag Intl knows, or null for none; anything else is refused. */
function locale(value: unknown): string | null {
  if (value === undefined || value === null) return null;
  if (typeof value !== "string" || value.length > 35) throw new ReportImageError("locale: Send a language tag such as en-GB.");
  try {
    return Intl.getCanonicalLocales(value)[0] ?? null;
  } catch {
    throw new ReportImageError("locale: Send a language tag such as en-GB.");
  }
}

/** Rows, single-event trace points and dashboard chart points: what a render's cost grows with. */
export function pointsOf(panel: ReportPanel): number {
  const chart = panel.chart;
  const chartPoints = chart
    ? (chart.glucoseData?.length ?? 0) + (chart.basalSeries?.length ?? 0) + (chart.bolusMarkers?.length ?? 0) + (chart.carbMarkers?.length ?? 0)
    : 0;
  return panel.rows.length + (panel.traces ?? []).reduce((sum, t) => sum + t.points.length, 0) + (panel.markers?.length ?? 0) + chartPoints;
}

/**
 * The API's request (`WebReportImageClient`): one panel and the parts of its report the drawing
 * reads (units, zone, colour mode, locale, clock), the size, theme and ratio. A missing height is
 * the primitive's own (`staticDefaultHeight`).
 */
export function readReportImageRequest(body: unknown): ReportImageRequest {
  if (!isObject(body)) throw new ReportImageError("Send a JSON object.");
  const hour12 = body.hour12;
  if (hour12 !== undefined && hour12 !== null && typeof hour12 !== "boolean") throw new ReportImageError("hour12: Send true or false.");
  const report = readReport({
    spec: { version: 1, title: null },
    units: body.units,
    timeZone: body.timeZone ?? null,
    glucoseColorMode: body.glucoseColorMode === "threshold" ? "threshold" : "continuous",
    locale: locale(body.locale),
    hour12: hour12 ?? null,
    summary: "",
    warnings: [],
    panels: [body.panel],
  });
  if (!report) throw new ReportImageError("panel: Send one panel and its units as render_report returns them.");
  return readOptions(report, report.panels[0], body);
}

/** The size, theme and ratio for a panel already chosen; the checks every request gets. */
export function readOptions(report: ReportResult, panel: ReportPanel, fields: Record<string, unknown>): ReportImageRequest {
  if (panel.status !== "ok") throw new ReportImageError(`Panel '${panel.id}' was not drawn: ${panel.sentence ?? panel.summary}`);
  if (!staticPrimitive(panel))
    throw new ReportImageError(`Panel '${panel.id}' is a ${panel.primitive}, which has no image: its figures are in the summary.`);
  const points = pointsOf(panel);
  if (points > REPORT_IMAGE_LIMITS.maxPoints)
    throw new ReportImageTooLargeError(
      `Panel '${panel.id}' has ${points} points, more than the ${REPORT_IMAGE_LIMITS.maxPoints} one image draws. Send a shorter range.`,
    );

  const width = whole(fields.width ?? 1200, "width", REPORT_IMAGE_LIMITS.minWidth, REPORT_IMAGE_LIMITS.maxWidth);
  const height = whole(fields.height ?? staticDefaultHeight(panel), "height", REPORT_IMAGE_LIMITS.minHeight, REPORT_IMAGE_LIMITS.maxHeight);
  const ratio = fields.devicePixelRatio ?? 1;
  if (ratio !== 1 && ratio !== 2) throw new ReportImageError("devicePixelRatio: Send 1 or 2.");
  const theme = fields.theme ?? "light";
  if (theme !== "light" && theme !== "dark") throw new ReportImageError("theme: Send light or dark.");
  return { report: { ...report, panels: [panel] }, panel, width, height, theme, devicePixelRatio: ratio };
}

/**
 * The worker bundle: `NOCTURNE_REPORT_IMAGE_WORKER` when it names a file, else in a production build
 * `build/report-image-worker/worker.mjs` under the app (the working directory, or `packages/app`
 * below it in the image). The dev server draws on this thread, from the source it is serving, rather
 * than from a bundle built from older source; `off` does the same anywhere (the render tests).
 */
function workerScript(): string | null {
  const named = process.env.NOCTURNE_REPORT_IMAGE_WORKER;
  if (named === "off") return null;
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- the operator's setting, not request data
  if (named) return existsSync(named) ? named : null;
  if (import.meta.env.DEV) return null;
  const candidates = [
    resolve(process.cwd(), "build/report-image-worker/worker.mjs"),
    resolve(process.cwd(), "packages/app/build/report-image-worker/worker.mjs"),
  ];
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- fixed paths under the working directory
  return candidates.find((path) => existsSync(path)) ?? null;
}

let pool: RenderPool<DrawJob> | null | undefined;
let warned = false;

/** Two workers (or one on a single core), four jobs waiting, fifteen seconds a job. */
function renderPool(): RenderPool<DrawJob> | null {
  if (pool !== undefined) return pool;
  const script = workerScript();
  pool = script
    ? new RenderPool<DrawJob>(script, { size: Math.min(2, availableParallelism()), queueLimit: 4, timeoutMs: 15_000 })
    : null;
  return pool;
}

/**
 * The panel as a PNG, drawn in a worker thread (`RenderPool`, which refuses with `RenderPoolBusyError`
 * when full). With no worker (the dev server, or a build without the worker bundle) it draws on this
 * thread; a production build says so once.
 */
export async function renderReportImage(request: ReportImageRequest): Promise<Buffer> {
  const job: DrawJob = { ...request, ...reportImageWords() };
  const workers = renderPool();
  if (!workers) {
    if (!warned && !import.meta.env.DEV && process.env.NOCTURNE_REPORT_IMAGE_WORKER !== "off") {
      console.warn("The report image worker is not built (pnpm run build:report-image-worker); images draw on the main thread.");
      warned = true;
    }
    return Buffer.from(drawPanel(job));
  }
  return Buffer.from(await workers.run(job));
}
