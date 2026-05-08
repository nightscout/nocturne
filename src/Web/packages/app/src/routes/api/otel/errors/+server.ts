import { json } from "@sveltejs/kit";
import type { RequestHandler } from "./$types";
import { trace, SpanStatusCode } from "@opentelemetry/api";
import { randomUUID } from "crypto";

const tracer = trace.getTracer("nocturne-web-client", "1.0.0");

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_FIELD_LENGTH = 4096;

export const POST: RequestHandler = async ({ request }) => {
  const contentLength = parseInt(request.headers.get("content-length") ?? "0", 10);
  if (contentLength > 16_384) {
    return new Response(null, { status: 413 });
  }

  let body: {
    message: string;
    stack?: string;
    url: string;
    errorId: string;
    userAgent?: string;
  };

  try {
    body = await request.json();
  } catch {
    return json({ error: "Invalid JSON" }, { status: 400 });
  }

  const errorId = UUID_RE.test(body.errorId) ? body.errorId : randomUUID();
  const message = (body.message ?? "").slice(0, MAX_FIELD_LENGTH);
  const stack = (body.stack ?? "").slice(0, MAX_FIELD_LENGTH);

  const span = tracer.startSpan("client-error", {
    attributes: {
      "error.id": errorId,
      "error.message": message,
      "error.stack": stack,
      "error.url": body.url,
      "http.user_agent":
        body.userAgent ?? request.headers.get("user-agent") ?? "",
    },
  });

  span.setStatus({ code: SpanStatusCode.ERROR, message });
  span.end();

  return new Response(null, { status: 204 });
};
