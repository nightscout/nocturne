import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { RequestEvent } from "@sveltejs/kit";
// @ts-expect-error SvelteKit publishes no types for this entry point.
import * as kitInternal from "@sveltejs/kit/internal/server";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";

// The real loaders read catalogs that only a wuchale build compiles.
vi.mock("wuchale/load-utils/server", () => ({
  loadLocales: async () => {},
  runWithLocale: (_locale: string, fn: () => unknown) => fn(),
}));
vi.mock("../../../locales/main.loader.server.svelte.js", () => ({
  key: "main",
  loadCount: 0,
  loadCatalog: () => ({}),
}));
vi.mock("../../../locales/js.loader.server.js", () => ({
  key: "js",
  loadCount: 0,
  loadCatalog: () => ({}),
}));
vi.mock("../../../locales/data.js", () => ({ locales: ["en"] }));
vi.mock("$env/dynamic/public", () => ({ env: {} }));
vi.mock("$lib/stores/appearance-store.svelte", () => ({
  LANGUAGE_COOKIE_NAME: "nocturne-language",
}));

const getSession = vi.fn();
const getStatus = vi.fn();

// Every API client the hooks build (session, status probe, per-request) answers from these.
vi.mock("$lib/server/api-client-factory", () => ({
  getApiBaseUrl: () => "http://api.test",
  createServerApiClient: () => ({
    oidc: { getSession: () => getSession() },
    status: { getStatus: () => getStatus() },
  }),
}));

const { handle } = await import("./hooks.server");
/** Sets up the request store SvelteKit's server wraps `handle` in, which sequence() reads. */
const withRequestStore = (kitInternal as { with_request_store: <T>(store: unknown, fn: () => T) => T })
  .with_request_store;
const { getRequestStatus } = await import("$lib/server/request-status");

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const signedIn = {
  IsAuthenticated: "true",
  [AUTH_COOKIE_NAMES.accessToken]: "access",
};

function requestEvent(host: string, pathname = "/", cookies: Record<string, string> = {}) {
  const jar = new Map(Object.entries(cookies));
  const url = `https://${host}${pathname}`;
  return {
    url: new URL(url),
    request: new Request(url, { headers: { "x-forwarded-host": host } }),
    cookies: {
      get: (name: string) => jar.get(name),
      getAll: () => [],
      set: () => {},
      delete: () => {},
    },
    locals: {},
    fetch,
    getClientAddress: () => "127.0.0.1",
  } as unknown as RequestEvent;
}

/**
 * Runs the hook chain with a resolve standing in for the layouts: it reads the tenant status the
 * way they do, so a request's status calls can be counted end to end.
 */
async function run(event: RequestEvent) {
  let layoutStatus: App.TenantStatus | null | undefined;
  const state = { tracing: { record_span: ({ fn }: { fn: (span: unknown) => unknown }) => fn({}) } };
  const response = await withRequestStore({ event, state }, () =>
    handle({
      event,
      resolve: async (resolved) => {
        layoutStatus = await getRequestStatus(resolved.locals);
        return new Response("page");
      },
    })
  );
  return { response, layoutStatus };
}

const ready = { status: "ok", tenantSlug: "acme", anonymousReadAccess: false };

let hostSeq = 0;
/** A host no earlier test has made ready, so the readiness cache starts cold for it. */
function freshHost() {
  hostSeq += 1;
  return `t${hostSeq}.nocturne.test`;
}

beforeEach(() => {
  getSession.mockReset();
  getStatus.mockReset();
  getSession.mockResolvedValue({ isAuthenticated: true, subjectId: "s1", name: "Sam" });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("hooks — the status probe", () => {
  it("starts the status probe and the session check together", async () => {
    const session = deferred<unknown>();
    const status = deferred<unknown>();
    getSession.mockReturnValue(session.promise);
    getStatus.mockReturnValue(status.promise);

    const pending = run(requestEvent(freshHost(), "/", signedIn));
    await vi.waitFor(() => expect(getSession).toHaveBeenCalledTimes(1));

    expect(getStatus).toHaveBeenCalledTimes(1);

    status.resolve(ready);
    session.resolve({ isAuthenticated: true, subjectId: "s1", name: "Sam" });
    await expect(pending).resolves.toMatchObject({ layoutStatus: ready });
  });

  it("hands the probe's answer to the layouts instead of asking again", async () => {
    getStatus.mockResolvedValue(ready);

    const { layoutStatus } = await run(requestEvent(freshHost(), "/", signedIn));

    expect(layoutStatus).toEqual(ready);
    expect(getStatus).toHaveBeenCalledTimes(1);
  });

  it("answers a host found ready within the TTL without a status call", async () => {
    const host = freshHost();
    getStatus.mockResolvedValue(ready);
    await run(requestEvent(host, "/", signedIn));
    getStatus.mockClear();

    const { layoutStatus } = await run(requestEvent(host, "/reports", signedIn));

    expect(getStatus).not.toHaveBeenCalled();
    expect(layoutStatus).toEqual(ready);
  });

  it("probes a ready host again once the TTL has passed", async () => {
    const host = freshHost();
    const now = Date.now();
    const clock = vi.spyOn(Date, "now").mockReturnValue(now);
    getStatus.mockResolvedValue(ready);
    await run(requestEvent(host, "/", signedIn));
    getStatus.mockClear();

    clock.mockReturnValue(now + 10_001);
    const later = { ...ready, anonymousReadAccess: true };
    getStatus.mockResolvedValue(later);
    const { layoutStatus } = await run(requestEvent(host, "/", signedIn));

    expect(getStatus).toHaveBeenCalledTimes(1);
    expect(layoutStatus).toEqual(later);
  });

  it("does not cache the API's error document", async () => {
    const host = freshHost();
    const failed = { status: "error", tenantSlug: "acme" };
    getStatus.mockResolvedValue(failed);
    const first = await run(requestEvent(host, "/", signedIn));
    getStatus.mockResolvedValue(ready);

    const second = await run(requestEvent(host, "/", signedIn));

    expect(first.layoutStatus).toEqual(failed);
    expect(second.layoutStatus).toEqual(ready);
    expect(getStatus).toHaveBeenCalledTimes(2);
  });

  it("keys the cache by host", async () => {
    getStatus.mockResolvedValue(ready);
    await run(requestEvent(freshHost(), "/", signedIn));
    getStatus.mockClear();

    await run(requestEvent(freshHost(), "/", signedIn));

    expect(getStatus).toHaveBeenCalledTimes(1);
  });

  it("redirects a host awaiting setup, and probes it again on the next request", async () => {
    const host = freshHost();
    getStatus.mockRejectedValue({
      status: 503,
      response: JSON.stringify({ error: "setup_required", recoveryMode: false }),
    });

    const first = await run(requestEvent(host, "/", signedIn));
    const second = await run(requestEvent(host, "/", signedIn));

    expect(first.response.status).toBe(303);
    expect(first.response.headers.get("location")).toBe("/setup");
    expect(second.response.headers.get("location")).toBe("/setup");
    expect(getStatus).toHaveBeenCalledTimes(2);
  });

  it("redirects a host in recovery mode to the recovery sign-in", async () => {
    getStatus.mockRejectedValue({
      status: 503,
      response: JSON.stringify({ error: "recovery_mode_active", recoveryMode: true }),
    });

    const { response } = await run(requestEvent(freshHost(), "/", signedIn));

    expect(response.headers.get("location")).toBe("/auth/recovery");
  });

  it("renders with no status when the probe fails without a redirect", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    getStatus.mockRejectedValue({ status: 500 });

    const { response, layoutStatus } = await run(requestEvent(freshHost(), "/", signedIn));

    expect(response.status).toBe(200);
    expect(layoutStatus).toBeNull();
    expect(getStatus).toHaveBeenCalledTimes(1);
  });

  it("does not probe the pages a failed probe redirects to", async () => {
    getStatus.mockRejectedValue({ status: 503, response: "{}" });

    const { response } = await run(requestEvent(freshHost(), "/setup", signedIn));

    expect(response.status).toBe(200);
  });
});
