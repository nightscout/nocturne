import { beforeEach, describe, expect, it, vi } from "vitest";

type SpanOptions = { attributes: Record<string, string> };

const startSpan = vi.fn((_name: string, _options: SpanOptions) => ({
  setStatus: vi.fn(),
  end: vi.fn(),
}));

vi.mock("@opentelemetry/api", () => ({
  trace: { getTracer: () => ({ startSpan }) },
  SpanStatusCode: { ERROR: 2 },
}));

const { POST } = await import("./+server");

function post(request: Request) {
  return (POST as unknown as (event: { request: Request }) => Promise<Response>)(
    { request },
  );
}

const URL_UNDER_TEST = "https://acme.nocturne.run/api/otel/errors";

/**
 * A request the way a runtime hands it to the handler: Content-Length set from
 * the body. `declaredLength` overrides it to model a caller that lies.
 */
function request(body: string, declaredLength?: string): Request {
  return new Request(URL_UNDER_TEST, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "content-length":
        declaredLength ?? String(new TextEncoder().encode(body).byteLength),
    },
    body,
  });
}

/** A body sent as a stream, so the request carries no Content-Length. */
function chunkedRequest(payload: string): Request {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(payload));
      controller.close();
    },
  });

  return new Request(URL_UNDER_TEST, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body,
    // @ts-expect-error duplex is required by undici for a stream body
    duplex: "half",
  });
}

const REPORT = {
  message: "boom",
  stack: "at foo",
  url: "https://acme.nocturne.run/dashboard",
  errorId: "8ba6dd4d-5f0d-4a0d-9a58-6a4e4bd2c111",
};

const OVERSIZED = JSON.stringify({ ...REPORT, stack: "x".repeat(32_768) });

describe("POST /api/otel/errors", () => {
  beforeEach(() => {
    startSpan.mockClear();
  });

  it("accepts a normal report", async () => {
    const response = await post(request(JSON.stringify(REPORT)));

    expect(response.status).toBe(204);
    expect(startSpan).toHaveBeenCalledTimes(1);
  });

  it("rejects an oversized body that understates its Content-Length", async () => {
    const response = await post(request(OVERSIZED, "42"));

    expect(response.status).toBe(413);
    expect(startSpan).not.toHaveBeenCalled();
  });

  it("rejects an oversized body that declares its real length", async () => {
    const response = await post(request(OVERSIZED));

    expect(response.status).toBe(413);
    expect(startSpan).not.toHaveBeenCalled();
  });

  it("rejects a chunked body, which declares no length at all", async () => {
    const response = await post(chunkedRequest(OVERSIZED));

    expect(response.status).toBe(411);
    expect(startSpan).not.toHaveBeenCalled();
  });

  it("rejects an unparseable Content-Length", async () => {
    const response = await post(request(JSON.stringify(REPORT), "not-a-number"));

    expect(response.status).toBe(411);
    expect(startSpan).not.toHaveBeenCalled();
  });

  it("clamps url the same way message and stack are clamped", async () => {
    // Each field is over the 4096 clamp while the whole body stays under the
    // 16 KB cap.
    const long = "y".repeat(5000);
    const response = await post(
      request(
        JSON.stringify({
          ...REPORT,
          message: long,
          stack: long,
          url: `https://acme.nocturne.run/?q=${long}`,
        }),
      ),
    );

    expect(response.status).toBe(204);
    const attributes = startSpan.mock.calls[0][1].attributes;
    expect(attributes["error.message"]).toHaveLength(4096);
    expect(attributes["error.stack"]).toHaveLength(4096);
    expect(attributes["error.url"]).toHaveLength(4096);
  });

  it("rejects invalid JSON", async () => {
    const response = await post(request("{not json"));

    expect(response.status).toBe(400);
    expect(startSpan).not.toHaveBeenCalled();
  });

  it("rejects a JSON body that is not an object", async () => {
    const response = await post(request("null"));

    expect(response.status).toBe(400);
    expect(startSpan).not.toHaveBeenCalled();
  });
});
