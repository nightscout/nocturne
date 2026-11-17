import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { RequestEvent } from "@sveltejs/kit";
// @ts-expect-error SvelteKit publishes no types for this entry point.
import * as kitInternal from "@sveltejs/kit/internal/server";
import { AUTH_COOKIE_NAMES } from "#lib/config/auth-cookies.js";
import { clearSessionCache } from "#lib/server/session-cache.js";
import type { ServerHttpClientOptions } from "#lib/server/api-client-factory.js";

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
vi.mock("#lib/stores/appearance-store.svelte.js", () => ({
  LANGUAGE_COOKIE_NAME: "nocturne-language",
}));

const env = vi.hoisted(() => ({
  instanceKey: undefined as string | undefined,
  devAutoLogin: undefined as string | undefined,
}));
// `$app/env/public` resolves to the same stub module, so this mocks both.
vi.mock("$app/env/private", () => ({
  PUBLIC_DEFAULT_LANGUAGE: undefined,
  PUBLIC_API_URL: undefined,
  MARKETING_URL: undefined,
  ALLOWED_FRAME_ANCESTORS: undefined,
  NOCTURNE_API_URL: undefined,
  get INSTANCE_KEY() {
    return env.instanceKey;
  },
  get NOCTURNE_DEV_AUTO_LOGIN() {
    return env.devAutoLogin;
  },
}));

const getSession = vi.fn();
const getStatus = vi.fn();
const clientOptions: (ServerHttpClientOptions | undefined)[] = [];

// Every API client the hooks build (session, status probe, per-request) answers from these. Raw
// calls go through the real createServerHttpClient to the stubbed global fetch.
vi.mock("#lib/server/api-client-factory.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("#lib/server/api-client-factory.js")>()),
  getApiBaseUrl: () => "http://api.test",
  createServerApiClient: (_base: string, _fetch: unknown, options?: ServerHttpClientOptions) => {
    clientOptions.push(options);
    return {
      oidc: { getSession: () => getSession() },
      status: { getStatus: () => getStatus() },
    };
  },
}));

const { handle, handleError } = await import("./hooks.server");
/** Sets up the request store SvelteKit's server wraps `handle` in, which sequence() reads. */
const withRequestStore = (kitInternal as { with_request_store: <T>(store: unknown, fn: () => T) => T })
  .with_request_store;
const { getRequestStatus } = await import("#lib/server/request-status.js");

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

/** Cookies the hooks set on the outgoing response, in order. */
const cookieWrites: { name: string; value: string; opts: Record<string, unknown> }[] = [];

function requestEvent(
  host: string,
  pathname = "/",
  cookies: Record<string, string> = {},
  routeId: string | null = "/(authenticated)"
) {
  const jar = new Map(Object.entries(cookies));
  const url = `https://${host}${pathname}`;
  return {
    url: new URL(url),
    route: { id: routeId },
    request: new Request(url, { headers: { "x-forwarded-host": host } }),
    cookies: {
      get: (name: string) => jar.get(name),
      getAll: () => [],
      set: (name: string, value: string, opts: Record<string, unknown>) => {
        cookieWrites.push({ name, value, opts });
      },
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
async function run(event: RequestEvent, page: () => Response = () => new Response("page")) {
  let layoutStatus: App.TenantStatus | null | undefined;
  const state = { tracing: { record_span: ({ fn }: { fn: (span: unknown) => unknown }) => fn({}) } };
  const response = await withRequestStore({ event, state }, () =>
    handle({
      event,
      resolve: async (resolved) => {
        layoutStatus = await getRequestStatus(resolved.locals);
        return page();
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
  clearSessionCache();
  getSession.mockReset();
  getStatus.mockReset();
  getSession.mockResolvedValue({ isAuthenticated: true, subjectId: "s1", name: "Sam" });
  clientOptions.length = 0;
  cookieWrites.length = 0;
  env.instanceKey = undefined;
  env.devAutoLogin = undefined;
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
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

  it("does not cache a setup_required document", async () => {
    // An apex that gets its first tenant must stop reading as tenantless on the next request.
    const host = freshHost();
    const tenantless = { status: "setup_required", tenantSlug: null };
    getStatus.mockResolvedValue(tenantless);
    const first = await run(requestEvent(host, "/", signedIn));
    getStatus.mockResolvedValue(ready);

    const second = await run(requestEvent(host, "/", signedIn));

    expect(first.layoutStatus).toEqual(tenantless);
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

describe("hooks — response headers", () => {
  it("puts the security headers on a page and on a redirect", async () => {
    getStatus.mockResolvedValue(ready);
    const page = await run(requestEvent(freshHost(), "/", signedIn));

    getStatus.mockRejectedValue({
      status: 503,
      response: JSON.stringify({ error: "recovery_mode_active", recoveryMode: true }),
    });
    const redirect = await run(requestEvent(freshHost(), "/", signedIn));

    for (const { response } of [page, redirect]) {
      expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
      expect(response.headers.get("X-Frame-Options")).toBe("DENY");
    }
  });

  it("keeps the style and font preloads in the Link header and drops the modulepreloads", async () => {
    getStatus.mockResolvedValue(ready);
    const css = '</_app/immutable/assets/0.A.css>; rel="preload"; as="style"; nopush';
    const font = '</_app/immutable/assets/f.A.woff2>; rel="preload"; as="font"; crossorigin; nopush';
    const html = '<head><link rel="modulepreload" href="/_app/immutable/chunks/a.js"></head>';
    const { response } = await run(requestEvent(freshHost(), "/", signedIn), () => {
      const page = new Response(html, { headers: { "content-type": "text/html" } });
      page.headers.set(
        "link",
        [css, '</_app/immutable/chunks/a.js>; rel="modulepreload"; nopush', font].join(", ")
      );
      return page;
    });

    expect(response.headers.get("link")).toBe([css, font].join(", "));
    expect(await response.text()).toBe(html);
  });

  it("removes the Link header when only modulepreloads were in it", async () => {
    getStatus.mockResolvedValue(ready);
    const { response } = await run(requestEvent(freshHost(), "/", signedIn), () => {
      const page = new Response("page");
      page.headers.set("link", '</_app/immutable/chunks/a.js>; rel="modulepreload"; nopush');
      return page;
    });

    expect(response.headers.has("link")).toBe(false);
  });
});

describe("hooks — demo sign-in within the request", () => {
  const demoStatus = { status: "ok", tenantSlug: "demo", anonymousReadAccess: false, isDemo: true };
  const expires = "Wed, 28 Oct 2026 12:00:00 GMT";
  const apiSetCookies = [
    // The host-scoped expiry SetSessionCookies writes ahead of the domain-wide cookie.
    `${AUTH_COOKIE_NAMES.accessToken}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`,
    `${AUTH_COOKIE_NAMES.accessToken}=minted-access; expires=${expires}; domain=.nocturne.test; path=/; secure; samesite=lax; httponly`,
    `${AUTH_COOKIE_NAMES.refreshToken}=minted-refresh; expires=${expires}; domain=.nocturne.test; path=/; secure; samesite=lax; httponly`,
    `IsAuthenticated=true; expires=${expires}; domain=.nocturne.test; path=/; secure; samesite=lax`,
  ];

  function minted(): Response {
    const headers = new Headers({ "content-type": "application/json" });
    for (const header of apiSetCookies) headers.append("set-cookie", header);
    return new Response(
      JSON.stringify({ accessToken: "minted-access", refreshToken: "minted-refresh", expiresInSeconds: 3600 }),
      { status: 200, headers }
    );
  }

  function stubApi(answer: () => Response | Promise<Response>) {
    const apiFetch = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => answer());
    vi.stubGlobal("fetch", apiFetch);
    return apiFetch;
  }

  it("signs a demo visitor in and renders the page with no redirect", async () => {
    getStatus.mockResolvedValue(demoStatus);
    getSession.mockResolvedValue({ isAuthenticated: true, subjectId: "demo-member", name: "Demo" });
    const apiFetch = stubApi(minted);
    const event = requestEvent(freshHost(), "/reports");

    const { response } = await run(event);

    expect(response.status).toBe(200);
    expect(apiFetch).toHaveBeenCalledTimes(1);
    expect(String(apiFetch.mock.calls[0][0])).toBe("http://api.test/api/v4/demo/session?format=json");
    expect(event.locals.isAuthenticated).toBe(true);
    expect(event.locals.user?.subjectId).toBe("demo-member");
    // The session check and the request's API client both carry the minted session.
    expect(clientOptions.filter((o) => o?.accessToken === "minted-access")).toHaveLength(2);
    expect(clientOptions.filter((o) => o?.refreshToken === "minted-refresh")).toHaveLength(2);
  });

  it("forwards the session cookies the API set, attributes intact", async () => {
    getStatus.mockResolvedValue(demoStatus);
    stubApi(minted);

    const { response } = await run(requestEvent(freshHost(), "/"));

    const expiry = new Date(expires);
    expect(cookieWrites).toEqual([
      {
        name: AUTH_COOKIE_NAMES.accessToken,
        value: "minted-access",
        opts: { path: "/", httpOnly: true, secure: true, sameSite: "lax", expires: expiry, domain: ".nocturne.test" },
      },
      {
        name: AUTH_COOKIE_NAMES.refreshToken,
        value: "minted-refresh",
        opts: { path: "/", httpOnly: true, secure: true, sameSite: "lax", expires: expiry, domain: ".nocturne.test" },
      },
      {
        name: "IsAuthenticated",
        value: "true",
        opts: { path: "/", httpOnly: false, secure: true, sameSite: "lax", expires: expiry, domain: ".nocturne.test" },
      },
    ]);
    expect(response.headers.getSetCookie()).toContain(apiSetCookies[0]);
  });

  it("asks without the instance key, naming the visitor by their signed address", async () => {
    env.instanceKey = "instance-secret";
    getStatus.mockResolvedValue(demoStatus);
    const apiFetch = stubApi(minted);

    await run(requestEvent(freshHost(), "/"));

    const headers = new Headers(apiFetch.mock.calls[0][1]?.headers);
    expect(headers.has("X-Instance-Key")).toBe(false);
    expect(headers.has("Cookie")).toBe(false);
    expect(headers.get("X-Nocturne-Client-Ip")).toBe("127.0.0.1");
    expect(headers.get("X-Nocturne-Client-Ip-Signature")).toMatch(/^[0-9a-f]{64}$/);
  });

  it.each([
    ["the API refuses", () => new Response(null, { status: 404 })],
    ["the API cannot be reached", () => Promise.reject(new TypeError("fetch failed"))],
    ["the API answers without tokens", () => Response.json({})],
  ])("leaves the visitor signed out for the layout's redirect when %s", async (_case, answer) => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    getStatus.mockResolvedValue(demoStatus);
    stubApi(answer);
    const event = requestEvent(freshHost(), "/");

    const { response } = await run(event);

    expect(response.status).toBe(200);
    expect(event.locals.isAuthenticated).toBe(false);
    expect(event.locals.mintedSession).toBeUndefined();
    expect(getSession).not.toHaveBeenCalled();
  });

  it("does not mint again once the redirect attempt has been spent", async () => {
    getStatus.mockResolvedValue(demoStatus);
    const apiFetch = stubApi(minted);

    await run(requestEvent(freshHost(), "/reports?__autologin=1"));

    expect(apiFetch).not.toHaveBeenCalled();
  });

  it.each([
    ["a tenant that is not a demo", { ...demoStatus, isDemo: false }, "/(authenticated)", undefined],
    ["a page outside the signed-in app", demoStatus, "/(unauthenticated)/auth/login", undefined],
    ["dev auto-login", demoStatus, "/(authenticated)", "true"],
    ["a request with no matched route", demoStatus, null, undefined],
  ])("does not mint for %s", async (_case, status, routeId, devAutoLogin) => {
    env.devAutoLogin = devAutoLogin;
    getStatus.mockResolvedValue(status);
    const apiFetch = stubApi(minted);

    await run(requestEvent(freshHost(), "/", {}, routeId));

    expect(apiFetch).not.toHaveBeenCalled();
  });

  it("does not mint for a request that is not a GET", async () => {
    getStatus.mockResolvedValue(demoStatus);
    const apiFetch = stubApi(minted);
    const event = requestEvent(freshHost(), "/");
    const post = { ...event, request: new Request(event.request, { method: "POST" }) };

    await run(post);

    expect(apiFetch).not.toHaveBeenCalled();
  });

  it("leaves a visitor holding a guest session to it", async () => {
    getStatus.mockResolvedValue(demoStatus);
    getSession.mockResolvedValue({ isAuthenticated: false });
    const apiFetch = stubApi(minted);

    await run(requestEvent(freshHost(), "/", { [AUTH_COOKIE_NAMES.guestSession]: "guest" }));

    expect(apiFetch).not.toHaveBeenCalled();
  });
});

describe("hooks — handleError", () => {
  const event = { request: new Request("https://sleepy.nocturne.test/reports/agp") } as unknown as RequestEvent;
  const quiet = () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(console, "log").mockImplementation(() => {});
  };
  afterEach(() => vi.restoreAllMocks());

  it("leaves an error(...) throw as the caller wrote it", async () => {
    const outcome = await handleError({
      kind: "app",
      error: { status: 409, message: "Already redeemed", issues: [] },
      event,
    });

    expect(outcome).toBeUndefined();
  });

  it("leaves a rejected remote argument to SvelteKit's own message", async () => {
    const outcome = await handleError({
      kind: "validation",
      error: { status: 400, message: "Bad Request" },
      issues: [],
      event,
    });

    expect(outcome).toBeUndefined();
  });

  it("keeps SvelteKit's message for its own errors and names the occurrence", async () => {
    quiet();
    const outcome = await handleError({ kind: "framework", error: { status: 404, message: "Not Found" }, event });

    expect(outcome).toMatchObject({ message: "Not Found", errorId: expect.any(String) });
  });

  it("names an unexpected error and carries its message", async () => {
    quiet();
    const outcome = await handleError({ kind: "unknown", error: new Error("boom"), event });

    expect(outcome).toMatchObject({ message: "boom", errorId: expect.any(String) });
  });
});
