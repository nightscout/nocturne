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

  it("caps an oversized chunked body, which declares no length at all", async () => {
    // The cap is enforced on bytes read, so this is rejected for its size rather
    // than for the absent Content-Length.
    const response = await post(chunkedRequest(OVERSIZED));

    expect(response.status).toBe(413);
    expect(startSpan).not.toHaveBeenCalled();
  });

  it("accepts a chunked report within the cap", async () => {
    // Content-Length is never consulted: a hop that re-frames the body as chunked
    // must not turn a legitimate browser error report into a rejection. The client
    // reporter swallows failures, so this would go dark silently.
    const response = await post(chunkedRequest(JSON.stringify(REPORT)));

    expect(response.status).toBe(204);
    expect(startSpan).toHaveBeenCalledTimes(1);
  });

  it("accepts a report whose Content-Length is unparseable", async () => {
    const response = await post(request(JSON.stringify(REPORT), "not-a-number"));

    expect(response.status).toBe(204);
    expect(startSpan).toHaveBeenCalledTimes(1);
  });

  it("does not 500 on a non-string field", async () => {
    // Anonymous and internet-reachable: a caller-supplied number where a string is
    // declared must not throw inside the handler.
    const response = await post(
      request(JSON.stringify({ ...REPORT, message: 1, stack: { a: 1 }, url: [] })),
    );

    expect(response.status).toBe(204);
    expect(startSpan).toHaveBeenCalledTimes(1);
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

  describe("Content-Security-Policy reports", () => {
    const attributes = (): Record<string, string> => startSpan.mock.calls[0][1].attributes;

    function cspRequest(report: Record<string, unknown>): Request {
      return new Request(URL_UNDER_TEST, {
        method: "POST",
        headers: { "Content-Type": "application/csp-report" },
        body: JSON.stringify({ "csp-report": report }),
      });
    }

    it("records a violation as a csp-violation span without an error status", async () => {
      const setStatus = vi.fn();
      startSpan.mockReturnValueOnce({ setStatus, end: vi.fn() });

      const response = await post(
        cspRequest({
          "document-uri": "https://acme.nocturne.run/reports?token=secret#frag",
          "blocked-uri": "inline",
          "violated-directive": "script-src-elem",
          "effective-directive": "script-src-elem",
          "original-policy": "script-src 'self'",
        }),
      );

      expect(response.status).toBe(204);
      expect(startSpan.mock.calls[0][0]).toBe("csp-violation");
      expect(attributes()).toEqual({
        "csp.blocked_uri": "inline",
        "csp.violated_directive": "script-src-elem",
        "csp.effective_directive": "script-src-elem",
      });
      expect(setStatus).not.toHaveBeenCalled();
    });

    it("keeps no share token from a share host in any attribute", async () => {
      await post(
        cspRequest({
          "document-uri": "https://tok3n.share.nocturne.run/?a=1",
          "blocked-uri": "https://tok3n.share.nocturne.run/assets/x.js",
          "violated-directive": "script-src-elem",
          "effective-directive": "script-src-elem",
        }),
      );

      expect(JSON.stringify(attributes())).not.toContain("tok3n");
      expect(attributes()["csp.blocked_uri"]).toBe("self");
    });

    it("keeps no guest code from the path in any attribute", async () => {
      await post(
        cspRequest({
          "document-uri": "https://acme.nocturne.run/guest/ABCD",
          "blocked-uri": "https://acme.nocturne.run/guest/ABCD",
          "violated-directive": "script-src-elem",
          "effective-directive": "script-src-elem",
        }),
      );

      expect(JSON.stringify(attributes())).not.toContain("ABCD");
      expect(attributes()).not.toHaveProperty("csp.document_uri");
    });

    it("replaces another share host with a placeholder and keeps other origins whole", async () => {
      await post(
        cspRequest({
          "document-uri": "https://acme.nocturne.run/",
          "blocked-uri": "wss://other.share.nocturne.run/hub?token=x",
        }),
      );
      await post(
        cspRequest({
          "document-uri": "https://acme.nocturne.run/",
          "blocked-uri": "https://evil.test/x.js?session=secret",
        }),
      );

      expect(startSpan.mock.calls[0][1].attributes["csp.blocked_uri"]).toBe("[share-host]");
      expect(startSpan.mock.calls[1][1].attributes["csp.blocked_uri"]).toBe("https://evil.test");
    });

    it("keeps a blocked scheme keyword when the document URI cannot be parsed", async () => {
      await post(cspRequest({ "document-uri": "not a url with spaces", "blocked-uri": "data" }));

      expect(attributes()["csp.blocked_uri"]).toBe("data");
    });

    it("clamps every reported field", async () => {
      const long = "z".repeat(5000);
      await post(cspRequest({ "violated-directive": long, "effective-directive": long }));

      expect(attributes()["csp.violated_directive"]).toHaveLength(4096);
      expect(attributes()["csp.effective_directive"]).toHaveLength(4096);
    });

    it("does not 500 on a malformed report and records nothing", async () => {
      for (const body of [{ "csp-report": "nope" }, { "csp-report": null }, { "csp-report": [1] }]) {
        const response = await post(
          new Request(URL_UNDER_TEST, {
            method: "POST",
            headers: { "Content-Type": "application/csp-report" },
            body: JSON.stringify(body),
          }),
        );
        expect(response.status).toBe(204);
      }
      expect(startSpan).not.toHaveBeenCalled();
    });

    it("rejects a csp report that is not JSON", async () => {
      const response = await post(
        new Request(URL_UNDER_TEST, {
          method: "POST",
          headers: { "Content-Type": "application/csp-report" },
          body: "{not json",
        }),
      );
      expect(response.status).toBe(400);
    });
  });
});
