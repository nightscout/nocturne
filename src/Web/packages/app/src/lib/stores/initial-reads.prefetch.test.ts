import { readFileSync } from "node:fs";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const clients = vi.hoisted(() => ({ http: undefined as unknown }));

vi.mock("$lib/api/client", async () => {
  const { ApiClient } = await import("$lib/api/api-client.generated");
  return { getApiClient: () => new ApiClient("", clients.http as never) };
});

vi.mock("svelte-sonner", () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import { RECENT_READINGS, RealtimeStore } from "./realtime-store.svelte";
import type { InitialReads } from "./initial-reads";

const SCRIPT = readFileSync(new URL("./initial-reads.prefetch.js", import.meta.url), "utf8");
const NOW = new Date("2026-10-10T12:34:56.789Z");

interface Call {
  url: string;
  headers: unknown;
  credentials?: string;
}

function emptyResponse(): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify({ data: [] }), { status: 200 }));
}

function runScript(): { calls: Call[]; window: { __nocturneInitialReads?: InitialReads } } {
  const calls: Call[] = [];
  const window: { __nocturneInitialReads?: InitialReads } = {};
  const fetchStub = (url: string, init?: RequestInit) => {
    calls.push({
      url,
      headers: init?.headers,
      credentials: init?.credentials,
    });
    return emptyResponse();
  };
  new Function("window", "fetch", SCRIPT)(window, fetchStub);
  return { calls, window };
}

describe("initial reads prefetch script", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("requests the URLs the realtime store requests, in the same shape", async () => {
    const { calls: prefetched, window: scriptWindow } = runScript();

    const storeCalls: Call[] = [];
    clients.http = {
      fetch: (url: string, init?: RequestInit) => {
        storeCalls.push({ url, headers: init?.headers });
        return emptyResponse();
      },
    };
    vi.stubGlobal("document", {
      visibilityState: "visible",
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    });
    vi.stubGlobal("window", {
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      __nocturneInitialReads: scriptWindow.__nocturneInitialReads,
    });
    const store = new RealtimeStore({
      url: "http://localhost",
      reconnectAttempts: 0,
      reconnectDelay: 0,
      maxReconnectDelay: 0,
      pingTimeout: 0,
      pingInterval: 0,
    });
    (store as unknown as { websocketClient: { connect(): void } }).websocketClient.connect = vi.fn();

    vi.setSystemTime(new Date(NOW.getTime() + 1_500));
    await store.initialize();
    store.destroy();

    expect(prefetched).toHaveLength(12);
    // The store also falls back to the latest readings when the day holds few; that read is conditional.
    const fallback = `/api/v4/glucose/sensor?limit=${RECENT_READINGS}`;
    expect(storeCalls.map((c) => c.url).filter((url) => url !== fallback).sort()).toEqual(
      prefetched.map((c) => c.url).sort()
    );
    for (const call of prefetched) {
      expect(call.headers).toEqual({ Accept: "application/json" });
      expect(call.credentials).toBe("include");
    }
    for (const call of storeCalls) expect(call.headers).toEqual({ Accept: "application/json" });
  });

  it("stashes the clock and one pending response per URL", () => {
    const { calls, window } = runScript();
    const stash = window.__nocturneInitialReads!;
    expect(stash.now).toBe(NOW.getTime());
    expect([...stash.responses.keys()].sort()).toEqual(calls.map((c) => c.url).sort());
  });

  it("does nothing when fetch is unavailable", () => {
    const window: { __nocturneInitialReads?: InitialReads } = {};
    new Function("window", "fetch", SCRIPT)(window, undefined);
    expect(window.__nocturneInitialReads).toBeUndefined();
  });

  it("does not throw when fetch throws", () => {
    const window: { __nocturneInitialReads?: InitialReads } = {};
    const run = () =>
      new Function("window", "fetch", SCRIPT)(window, () => {
        throw new Error("blocked");
      });
    expect(run).not.toThrow();
  });
});
