import { command, getRequestEvent } from "$app/server";
import { error } from "@sveltejs/kit";
import { z } from "zod";
import {
  readReportImageRequest,
  renderReportImage,
  REPORT_IMAGE_LIMITS,
  ReportImageError,
} from "#lib/server/report-image/render.js";

/**
 * A panel of a report the page already holds, drawn as a PNG by the renderer the API's
 * render_report_image tool calls (routes/api/internal/report-image), at that tool's default size and
 * theme, so a member saves the picture an assistant gets. It reads no data; the result is the one
 * the page ran under the member's own access. Answers the PNG as base64.
 */
export const drawReportPanelImage = command(
  z.object({
    result: z.unknown(),
    panelId: z.string().min(1).max(64),
  }),
  async ({ result, panelId }) => {
    const { locals } = getRequestEvent();
    if (!locals.isAuthenticated) error(401, "Sign in to download a report image.");
    if (JSON.stringify(result ?? null).length > REPORT_IMAGE_LIMITS.maxBodyBytes)
      error(413, "The report is too large to draw. Send one panel.");

    try {
      const png = renderReportImage(
        readReportImageRequest({ result, panelId, width: 1200, height: 600, theme: "light", devicePixelRatio: 1 }),
      );
      return png.toString("base64");
    } catch (err) {
      if (err instanceof ReportImageError) error(400, err.message);
      throw err;
    }
  },
);
