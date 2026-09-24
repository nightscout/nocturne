import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("$env/dynamic/public", () => ({ env: {} }));

const { authenticateGuestSession } = await import("./guest-session-auth");
const { AUTH_COOKIE_NAMES } = await import("$lib/config/auth-cookies");

/**
 * Answers `/api/auth/oidc/session` the way the API's handler chain does: a
 * valid guest cookie authenticates as that guest, and failing that, the
 * instance key and service marker authenticate as the instance service with
 * `*`. Anything else is anonymous.
 */
function sessionApi(validGuestCookie: string) {
  return vi.fn<typeof fetch>(async (_url, init) => {
    const headers = new Headers(init?.headers);
    const cookie = headers.get("Cookie") ?? "";
    const body = cookie.includes(
      `${AUTH_COOKIE_NAMES.guestSession}=${validGuestCookie}`
    )
      ? { isAuthenticated: true, permissions: ["api:entries:read"] }
      : headers.has("X-Instance-Key") && headers.has("X-Instance-Service")
        ? { isAuthenticated: true, permissions: ["*"] }
        : { isAuthenticated: false };
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  });
}

function guestRequest(guestCookie: string) {
  return {
    request: new Request("https://sleepy.nocturne.run/"),
    cookies: {
      get: (name: string) =>
        name === AUTH_COOKIE_NAMES.guestSession ? guestCookie : undefined,
    },
    locals: { isAuthenticated: false, user: null },
  } as unknown as Parameters<typeof authenticateGuestSession>[0];
}

beforeEach(() => {
  process.env.INSTANCE_KEY = "an-instance-signing-secret";
});

describe("authenticateGuestSession", () => {
  it("leaves a request with an undecryptable guest cookie anonymous", async () => {
    const api = sessionApi("real-guest-session");
    const event = guestRequest("garbage");

    await authenticateGuestSession(event, "http://api.internal", api);

    expect(event.locals.isAuthenticated).toBe(false);
    expect(event.locals.isGuestSession).toBeUndefined();
    const sent = new Headers(api.mock.calls[0][1]?.headers);
    expect(sent.has("X-Instance-Key")).toBe(false);
    expect(sent.has("X-Instance-Service")).toBe(false);
  });

  it("signs in a request the API accepts as a guest", async () => {
    const event = guestRequest("real-guest-session");

    await authenticateGuestSession(
      event,
      "http://api.internal",
      sessionApi("real-guest-session")
    );

    expect(event.locals.isAuthenticated).toBe(true);
    expect(event.locals.isGuestSession).toBe(true);
    expect(event.locals.user?.permissions).toEqual(["api:entries:read"]);
  });
});
