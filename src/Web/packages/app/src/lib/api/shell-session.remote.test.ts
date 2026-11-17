import { describe, it, expect, vi } from "vitest";
import { errorStatus } from "#lib/forms/submit-error.js";

let locals: Record<string, unknown>;

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({ locals }),
  query: (fn: unknown) => fn,
}));

const { getShellSession } = await import("./shell-session.remote");

function api(overrides: { permissions?: () => Promise<unknown>; status?: () => Promise<unknown> } = {}) {
  return {
    myPermissions: { getMyPermissions: overrides.permissions ?? (async () => ({ scopes: ["glucose.read"] })) },
    status: { getStatus: overrides.status ?? (async () => ({ anonymousReadAccess: true })) },
  };
}

async function rejectionStatus(call: () => Promise<unknown>): Promise<number | undefined> {
  try {
    await call();
  } catch (rejection) {
    return errorStatus(rejection);
  }
  throw new Error("expected the probe to reject");
}

const outage = { status: 502 };
const unreachable = new TypeError("fetch failed");

describe("getShellSession", () => {
  it("reports the signed-in member the hooks resolved", async () => {
    locals = {
      isAuthenticated: true,
      user: { subjectId: "s1" },
      effectivePermissions: ["*"],
      limitTo24Hours: true,
      apiClient: api(),
    };

    await expect(getShellSession()).resolves.toMatchObject({
      subjectId: "s1",
      scopes: ["*"],
      limitTo24Hours: true,
    });
  });

  it("rejects when the hooks could not read the session, rather than reporting a sign-out", async () => {
    locals = { isAuthenticated: false, user: null, sessionUnreadable: true, apiClient: api() };

    await expect(rejectionStatus(() => getShellSession())).resolves.toBe(503);
  });

  it.each([
    ["a 5xx", outage],
    ["an unreachable API", unreachable],
  ])("rejects on a guest whose grant lookup hit %s, rather than reporting no grant", async (_name, failure) => {
    locals = {
      isAuthenticated: true,
      isGuestSession: true,
      user: { subjectId: "guest" },
      apiClient: api({ permissions: async () => Promise.reject(failure) }),
    };

    await expect(rejectionStatus(() => getShellSession())).resolves.toBe(503);
  });

  it("rejects on a share host whose status lookup hit an outage, rather than reporting the link revoked", async () => {
    locals = {
      isAuthenticated: false,
      isShareHost: true,
      apiClient: api({ status: async () => Promise.reject(outage) }),
    };

    await expect(rejectionStatus(() => getShellSession())).resolves.toBe(503);
  });

  it("reports a share link the API no longer grants as revoked", async () => {
    locals = {
      isAuthenticated: false,
      isShareHost: true,
      apiClient: api({ status: async () => ({ anonymousReadAccess: false }) }),
    };

    await expect(getShellSession()).resolves.toMatchObject({ shareLinkLive: false });
  });

  it("reports no grant when the API refuses the lookup, which is a verdict", async () => {
    locals = {
      isAuthenticated: false,
      isShareHost: true,
      apiClient: api({ permissions: async () => Promise.reject({ status: 403 }) }),
    };

    await expect(getShellSession()).resolves.toMatchObject({ scopes: [] });
  });
});
