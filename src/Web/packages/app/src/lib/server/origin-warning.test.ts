import { describe, expect, it } from "vitest";
import { EventEmitter } from "node:events";

// @ts-expect-error - plain JS module shipped beside server.js, no types
import { warnOnOriginMismatch } from "../../../server-origin-warning.js";

/** A response whose status is chosen by the test and whose `finish` the test fires. */
class FakeResponse extends EventEmitter {
  statusCode: number;

  constructor(statusCode: number) {
    super();
    this.statusCode = statusCode;
  }
}

function fakeRequest(origin: string | undefined, forwarded: { proto: string; host: string }) {
  return {
    method: "POST",
    url: "/setup",
    headers: {
      origin,
      "x-forwarded-proto": forwarded.proto,
      "x-forwarded-host": forwarded.host,
    },
  };
}

/** Runs one request through the warner and returns whatever it logged. */
function report(
  origin: string | undefined,
  forwarded: { proto: string; host: string },
  statusCode: number,
  reported = new Set<string>()
): string[] {
  const logged: string[] = [];
  const res = new FakeResponse(statusCode);
  warnOnOriginMismatch(fakeRequest(origin, forwarded), res, {
    reported,
    warn: (m: string) => logged.push(m),
  });
  res.emit("finish");
  return logged;
}

const proxied = { proto: "http", host: "192.168.1.121:8080" };
const matching = { proto: "https", host: "nocturne.example.com" };

describe("warnOnOriginMismatch", () => {
  it("names both origins when a mismatched request is refused", () => {
    const logged = report("https://nocturne.example.com", proxied, 403);

    expect(logged).toHaveLength(1);
    expect(logged[0]).toContain("https://nocturne.example.com");
    expect(logged[0]).toContain("http://192.168.1.121:8080");
    expect(logged[0]).toContain("/docs/installation/reverse-proxy");
  });

  it("stays quiet when the origins agree, whatever the status", () => {
    expect(report("https://nocturne.example.com", matching, 403)).toEqual([]);
  });

  it("stays quiet when the mismatch was not what refused the request", () => {
    // An authorization 403 reaches here too; only a rejection worth explaining
    // pairs a mismatch with the 403.
    expect(report("https://nocturne.example.com", proxied, 200)).toEqual([]);
    expect(report("https://nocturne.example.com", proxied, 401)).toEqual([]);
  });

  it("ignores requests that carry no Origin at all", () => {
    expect(report(undefined, proxied, 403)).toEqual([]);
  });

  it("reports a given pairing once, however often it recurs", () => {
    const reported = new Set<string>();
    const first = report("https://nocturne.example.com", proxied, 403, reported);
    const second = report("https://nocturne.example.com", proxied, 403, reported);

    expect(first).toHaveLength(1);
    expect(second).toEqual([]);
  });

  it("stops reporting once distinct pairings hit the cap", () => {
    const reported = new Set<string>();
    for (let i = 0; i < 32; i++) {
      expect(report(`https://host-${i}.example.com`, proxied, 403, reported)).toHaveLength(1);
    }

    // Past the cap the set stops growing, so nothing further is logged — the
    // failure this guards is a cap that silently degrades into logging always.
    expect(report("https://one-too-many.example.com", proxied, 403, reported)).toEqual([]);
    expect(reported.size).toBe(32);
  });
});
