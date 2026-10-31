import { command, getRequestEvent } from "$app/server";
import { error } from "@sveltejs/kit";
import { z } from "zod";
import { readReport } from "@nocturne/charts/core";
import { AUTH_COOKIE_NAMES } from "#lib/config/auth-cookies.js";
import {
  readOptions,
  renderReportImage,
  ReportImageError,
  ReportImageTooLargeError,
} from "#lib/server/report-image/render.js";
import { RenderPoolBusyError } from "#lib/server/report-image/pool.js";

/** What the page shows for a refusal; the page writes the words. */
export type ReportImageFailure = "not_member" | "rate_limited" | "not_drawable" | "too_large" | "busy";

/** Images a signed-in member may make a minute, per member and tenant host. */
const PER_MINUTE = 6;
const recent = new Map<string, number[]>();

function withinRate(key: string, now = Date.now()): boolean {
  const kept = (recent.get(key) ?? []).filter((at) => now - at < 60_000);
  if (kept.length >= PER_MINUTE) {
    recent.set(key, kept);
    return false;
  }
  kept.push(now);
  recent.set(key, kept);
  if (recent.size > 10_000) recent.clear();
  return true;
}

/** The longest spec sent: the API's custom report run refuses more. */
const MAX_SPEC_CHARS = 16 * 1024;

/**
 * A chart panel of a custom report as the PNG the API's render_report_image tool gets: the spec is run
 * again by the API's custom report run under this member's own access (tenant, scopes, history clamp,
 * audit row), and the panel of that result is drawn by the same renderer at the tool's defaults. No
 * report data is taken from the page. Only a member's own session may ask: a share or guest link may
 * not. Answers the PNG as base64; a refusal is an error whose message is a `ReportImageFailure`.
 */
export const drawReportPanelImage = command(
  z.object({
    spec: z.unknown().refine((spec) => JSON.stringify(spec ?? null).length <= MAX_SPEC_CHARS),
    panelId: z.string().min(1).max(64),
  }).strict(),
  async ({ spec, panelId }) => {
    const event = getRequestEvent();
    const { locals, cookies, url } = event;
    const member = locals.isAuthenticated && !locals.isShareHost
      && (!!cookies.get(AUTH_COOKIE_NAMES.accessToken) || !!locals.mintedSession);
    if (!member || !locals.user) error(403, "not_member" satisfies ReportImageFailure);
    if (!withinRate(`${url.host}:${locals.user.subjectId}`)) error(429, "rate_limited" satisfies ReportImageFailure);

    const run = await locals.apiClient.customReports.runCustomReport({ spec });
    const report = readReport(run.result);
    const panel = report?.panels.find((p) => p.id === panelId);
    if (!report || !panel || (run.errors ?? []).length > 0) error(400, "not_drawable" satisfies ReportImageFailure);

    try {
      const png = await renderReportImage(readOptions(report, panel, { theme: "light" }));
      return png.toString("base64");
    } catch (err) {
      if (err instanceof ReportImageTooLargeError) error(413, "too_large" satisfies ReportImageFailure);
      if (err instanceof ReportImageError) error(400, "not_drawable" satisfies ReportImageFailure);
      if (err instanceof RenderPoolBusyError) error(503, "busy" satisfies ReportImageFailure);
      throw err;
    }
  },
);
