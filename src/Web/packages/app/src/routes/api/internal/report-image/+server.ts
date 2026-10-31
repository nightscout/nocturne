import type { RequestHandler } from "./$types";
import { isTrustedInstanceRequest } from "#lib/server/instance-key.js";
import {
  readReportImageRequest,
  renderReportImage,
  REPORT_IMAGE_LIMITS,
  ReportImageError,
} from "#lib/server/report-image/render.js";

const json = (status: number, error: string) =>
  new Response(JSON.stringify({ error }), { status, headers: { "Content-Type": "application/json" } });

/** The body as text, or null when it is longer than the limit; stops reading at the limit. */
async function readCapped(request: Request, limit: number): Promise<string | null> {
  const declared = Number(request.headers.get("content-length") ?? "");
  if (Number.isFinite(declared) && declared > limit) return null;
  if (!request.body) return "";
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > limit) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }
  return Buffer.concat(chunks).toString("utf8");
}

/**
 * A report panel as a PNG, for the API's render_report_image tool. It draws only the result it is
 * sent and reads no data. Like the bot dispatch route, it is reachable from the internet through the
 * gateway's `/api/**` route and is exempted from the proxy in hooks.server.ts, so the caller must
 * present the instance key before anything is read.
 */
export const POST: RequestHandler = async ({ request }) => {
  if (!isTrustedInstanceRequest(request)) {
    return new Response(null, { status: 401 });
  }

  const text = await readCapped(request, REPORT_IMAGE_LIMITS.maxBodyBytes);
  if (text === null) return json(413, "The report is too large to draw. Send one panel.");

  let body: unknown;
  try {
    body = JSON.parse(text);
  } catch {
    return json(400, "Send the request as JSON.");
  }

  try {
    const png = renderReportImage(readReportImageRequest(body));
    return new Response(new Uint8Array(png), {
      status: 200,
      headers: { "Content-Type": "image/png", "Cache-Control": "no-store" },
    });
  } catch (err) {
    if (err instanceof ReportImageError) return json(400, err.message);
    console.error("Report image render failed:", err);
    return json(500, "The image could not be drawn.");
  }
};
