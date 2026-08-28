import { describe, expect, it } from "vitest";
import { EventEmitter } from "node:events";

// @ts-expect-error - plain JS module shipped beside server.js, no types
import { createReportBudget, warnOnOriginMismatch } from "../../../server-origin-warning.js";

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
  budget = createReportBudget(),
  at = 0
): string[] {
  const logged: string[] = [];
  const res = new FakeResponse(statusCode);
  warnOnOriginMismatch(fakeRequest(origin, forwarded), res, {
    budget,
    warn: (m: string) => logged.push(m),
    now: () => at,
  });
  res.emit("finish");
  return logged;
}

/** Matches REPORT_WINDOW_MS in the module under test. */
const WINDOW_MS = 10 * 60_000;

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

  it("treats a default port as the same origin", () => {
    // SvelteKit compares URL.origin, which drops :443 on https. Comparing raw
    // strings here would blame the proxy for a deployment that is correct.
    expect(
      report("https://nocturne.example.com", { proto: "https", host: "nocturne.example.com:443" }, 403)
    ).toEqual([]);
  });

  it("still reports when only the scheme differs", () => {
    expect(
      report("https://nocturne.example.com", { proto: "http", host: "nocturne.example.com" }, 403)
    ).toHaveLength(1);
  });

  it("ignores requests that carry no Origin at all", () => {
    expect(report(undefined, proxied, 403)).toEqual([]);
  });

  it("reports a given pairing once, however often it recurs", () => {
    const budget = createReportBudget();
    const first = report("https://nocturne.example.com", proxied, 403, budget);
    const second = report("https://nocturne.example.com", proxied, 403, budget);

    expect(first).toHaveLength(1);
    expect(second).toEqual([]);
  });

  it("stops reporting once distinct pairings hit the cap", () => {
    const budget = createReportBudget();
    for (let i = 0; i < 32; i++) {
      expect(report(`https://host-${i}.example.com`, proxied, 403, budget)).toHaveLength(1);
    }

    // Past the cap the set stops growing, so nothing further is logged — the
    // failure this guards is a cap that silently degrades into logging always.
    expect(report("https://one-too-many.example.com", proxied, 403, budget)).toEqual([]);
    expect(budget.reported.size).toBe(32);
  });

  it("speaks again in the next window after a scanner spends the cap", () => {
    // The cap is what stops a flood; without expiry it is also what permanently
    // silences the one message an operator needs. A scanner burns all 32 slots on
    // origins of its choosing, and the operator's own pairing still gets reported
    // once the window rolls over.
    const budget = createReportBudget();
    for (let i = 0; i < 32; i++) {
      report(`https://scanner-${i}.example.com`, proxied, 403, budget, 1000);
    }
    expect(report("https://nocturne.example.com", proxied, 403, budget, 1000)).toEqual([]);

    const next = report("https://nocturne.example.com", proxied, 403, budget, 1000 + WINDOW_MS);
    expect(next).toHaveLength(1);
    expect(next[0]).toContain("https://nocturne.example.com");
    expect(budget.reported.size).toBe(1);
  });

  it("keeps deduplicating within a window as the clock advances", () => {
    // Expiry must be windowed, not per-call: a pairing seen twice inside one window
    // is still reported once. The failure this guards is a reset that fires on every
    // request and turns the dedupe off.
    const budget = createReportBudget();
    expect(report("https://nocturne.example.com", proxied, 403, budget, 1000)).toHaveLength(1);
    expect(
      report("https://nocturne.example.com", proxied, 403, budget, 1000 + WINDOW_MS - 1)
    ).toEqual([]);
  });
});
