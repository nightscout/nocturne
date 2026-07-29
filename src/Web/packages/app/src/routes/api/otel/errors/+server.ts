import { json } from "@sveltejs/kit";
import type { RequestHandler } from "./$types";
import { trace, SpanStatusCode } from "@opentelemetry/api";
import { randomUUID } from "crypto";

const tracer = trace.getTracer("nocturne-web-client", "1.0.0");

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_FIELD_LENGTH = 4096;
const MAX_BODY_BYTES = 16_384;

/**
 * Clamps a reported field to a fixed length before it reaches a span attribute.
 * Non-string input reads as absent: the body is caller-supplied JSON, so a number
 * or object here would otherwise throw inside the handler and turn an anonymous
 * report into a 500 plus a logged stack.
 */
const clampField = (value: unknown): string =>
  typeof value === "string" ? value.slice(0, MAX_FIELD_LENGTH) : "";

/**
 * Reads the request body with a hard byte ceiling, returning null once the
 * ceiling is passed. Enforcing the limit on bytes actually read means a chunked
 * request (no Content-Length) or a request that understates its length cannot
 * exceed the cap.
 */
async function readBounded(
  request: Request,
  maxBytes: number,
): Promise<string | null> {
  // No body stream at all reads as empty, which the JSON parse below rejects.
  const reader = request.body?.getReader();
  if (!reader) return "";

  const chunks: Uint8Array[] = [];
  let total = 0;

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxBytes) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }

  const buffer = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    buffer.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder().decode(buffer);
}

// Anonymous by design: browser error reports arrive before (or instead of) any
// session, so this endpoint cannot require the instance key. Volume is bounded
// per request by MAX_BODY_BYTES and by the fixed span-attribute clamps.
export const POST: RequestHandler = async ({ request }) => {
  // Content-Length is deliberately not consulted. readBounded enforces the cap on
  // bytes actually read, so a declared length adds no protection, and requiring one
  // would reject any report whose body a hop re-framed as chunked — silently, since
  // the reporter in hooks.client.ts swallows failures.
  const raw = await readBounded(request, MAX_BODY_BYTES);
  if (raw === null) {
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
    body = JSON.parse(raw);
  } catch {
    return json({ error: "Invalid JSON" }, { status: 400 });
  }

  if (typeof body !== "object" || body === null) {
    return json({ error: "Invalid JSON" }, { status: 400 });
  }

  const errorId = UUID_RE.test(body.errorId) ? body.errorId : randomUUID();
  const message = clampField(body.message);

  const span = tracer.startSpan("client-error", {
    attributes: {
      "error.id": errorId,
      "error.message": message,
      "error.stack": clampField(body.stack),
      "error.url": clampField(body.url),
      "http.user_agent": clampField(
        body.userAgent ?? request.headers.get("user-agent") ?? undefined,
      ),
    },
  });

  span.setStatus({ code: SpanStatusCode.ERROR, message });
  span.end();

  return new Response(null, { status: 204 });
};
