import { describe, expect, it } from "vitest";
import { isRedirect, type Cookies } from "@sveltejs/kit";
import { classifyHost, isTenantlessHost } from "$lib/server/tenantless-host";
import { load } from "./+layout.server";

/**
 * The authenticated shell's load, exercised end to end from a hostname rather than through the
 * host-classification helpers it depends on.
 *
 * Those helpers were unit-tested and correct while the shell still bounced every tenantless host
 * to /setup, because the redirect lived above the branch that reads them — a shape no test over
 * classifyHost/isTenantlessHost could see. These drive the load itself, one host situation per
 * test, and assert where the visitor ends up.
 */

type LoadEvent = Parameters<typeof load>[0];

const BASE = "nocturne.run";

interface Situation {
  /** The request host, classified with the real helpers to produce the layout's `tenantless`. */
  host: string;
  /** Whether the API auto-resolved a sole tenant behind the apex. */
  apexResolvesTenant?: boolean;
  /** Slugs the operator reserved for the dashboard (none by default). */
  dashboardSlugs?: string[];
  /** The /api/v4/status document. */
  status: { status?: string; tenantSlug?: string | null };
  /** The passkey auth-status answer; a number rejects with that HTTP status. */
  authStatus: { onboardingCompleted?: boolean } | number;
  signedIn?: boolean;
  pathname?: string;
  /** The caller's effective permissions, as /api/v4/me/permissions reports them. */
  permissions?: string[];
}

function runLoad(situation: Situation) {
  const { kind } = classifyHost(situation.host, BASE, situation.dashboardSlugs ?? []);
  const tenantless = isTenantlessHost(kind, situation.apexResolvesTenant ?? false);

  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- a stub of the three Cookies methods this load touches; implementing the full interface would say nothing
  const cookies = {
    get: () => undefined,
    set: () => {},
    delete: () => {},
  } as unknown as Cookies;

  const signedIn = situation.signedIn ?? true;
  const locals = {
    isGuestSession: false,
    isShareHost: false,
    isAuthenticated: signedIn,
    user: signedIn ? { subjectId: "s1", name: "Sam" } : null,
    effectivePermissions: situation.permissions ?? ["*"],
    apiClient: {
      status: { getStatus: async () => situation.status },
      passkey: {
        getAuthStatus: async () => {
          if (typeof situation.authStatus === "number") throw { status: situation.authStatus };
          return situation.authStatus;
        },
      },
    },
  };

  const event = {
    locals,
    cookies,
    url: new URL(`https://${situation.host}${situation.pathname ?? "/"}`),
    parent: async () => ({ tenantless }),
  };

  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- the load reads five fields of the request event; the rest of SvelteKit's ServerLoadEvent is not reachable from here
  return load(event as unknown as LoadEvent);
}

/** The page data the load returned, for situations that render rather than redirect. */
async function loadedData(situation: Situation): Promise<{ canViewRealtimeData: boolean }> {
  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- the load's declared return includes void, for the paths that throw a redirect; these situations render
  return (await runLoad(situation)) as { canViewRealtimeData: boolean };
}

/** The Location of the redirect the load threw, or null if it returned page data. */
async function redirectLocation(situation: Situation): Promise<string | null> {
  try {
    await runLoad(situation);
    return null;
  } catch (err) {
    if (isRedirect(err)) return err.location;
    throw err;
  }
}

/** A host that resolves no tenant: tenants exist, this host just names none of them. */
const populatedTenantless = {
  status: { status: "setup_required", tenantSlug: null },
  authStatus: 404,
} as const;

describe("(authenticated) layout load — where each host situation lands", () => {
  it("renders the dashboard on an apex with several tenants", async () => {
    // The API reports "setup_required" for any request that resolves no tenant, and a
    // multi-tenant apex resolves none. Redirecting on that alone put the fresh-install wizard
    // in front of a production deployment and the dashboard never rendered at all.
    await expect(
      redirectLocation({ host: BASE, ...populatedTenantless })
    ).resolves.toBeNull();
  });

  it("renders the dashboard on a reserved dashboard slug", async () => {
    await expect(
      redirectLocation({
        host: `dashboard.${BASE}`,
        dashboardSlugs: ["dashboard"],
        // The apex behind it resolves a tenant; the reserved slug is the dashboard regardless.
        apexResolvesTenant: true,
        ...populatedTenantless,
      })
    ).resolves.toBeNull();
  });

  it("renders the tenant app on an apex that resolves its sole tenant", async () => {
    await expect(
      redirectLocation({
        host: BASE,
        apexResolvesTenant: true,
        status: { status: "ok", tenantSlug: "acme" },
        authStatus: { onboardingCompleted: true },
      })
    ).resolves.toBeNull();
  });

  it("still sends an apex with zero tenants to setup", async () => {
    // The one host situation that IS a fresh install. The API serves 503 setup_required when no
    // tenant exists anywhere, which is what separates it from the multi-tenant apex above.
    await expect(
      redirectLocation({
        host: BASE,
        status: { status: "setup_required", tenantSlug: null },
        authStatus: 503,
      })
    ).resolves.toBe("/setup");
  });

  it("still sends a tenant host awaiting first-run setup to setup", async () => {
    await expect(
      redirectLocation({
        host: `acme.${BASE}`,
        status: { status: "setup_required", tenantSlug: "acme" },
        authStatus: { onboardingCompleted: false },
      })
    ).resolves.toBe("/setup");
  });

  it("sends a signed-out visitor on a tenantless host to login, not to the wizard", async () => {
    await expect(
      redirectLocation({ host: BASE, signedIn: false, ...populatedTenantless })
    ).resolves.toBe("/auth/login?returnUrl=%2F");
  });

  it("bounces a tenant-scoped route on a tenantless host back to the overview", async () => {
    await expect(
      redirectLocation({
        host: BASE,
        pathname: "/settings/account",
        ...populatedTenantless,
      })
    ).resolves.toBe("/");
  });

  it("sends a signed-out visitor to login before the tenantless route guard runs", async () => {
    // Order matters: the guard would otherwise swallow the returnUrl and drop the visitor on a
    // page they still cannot see.
    await expect(
      redirectLocation({
        host: BASE,
        signedIn: false,
        pathname: "/settings/account",
        ...populatedTenantless,
      })
    ).resolves.toBe("/auth/login?returnUrl=%2Fsettings%2Faccount");
  });
});

describe("(authenticated) layout load — realtime data", () => {
  it("withholds realtime data on a tenantless host from a platform admin holding *", async () => {
    // On a tenantless host /api/v4/me/permissions reports the raw JWT scopes, and a platform
    // admin's carry "*" — which reads as glucose access even though no tenant resolved. Left
    // ungated, that session opens a websocket that reconnects forever and bursts a day of
    // entries, devicestatus, profile and tracker reads against a host that answers 404.
    const data = await loadedData({ host: BASE, ...populatedTenantless });
    expect(data.canViewRealtimeData).toBe(false);
  });

  it("withholds realtime data on a reserved dashboard slug", async () => {
    const data = await loadedData({
      host: `dashboard.${BASE}`,
      dashboardSlugs: ["dashboard"],
      apexResolvesTenant: true,
      ...populatedTenantless,
    });
    expect(data.canViewRealtimeData).toBe(false);
  });

  it("enables realtime data on a tenant host for a caller with glucose read", async () => {
    const data = await loadedData({
      host: `acme.${BASE}`,
      permissions: ["glucose.read"],
      status: { status: "ok", tenantSlug: "acme" },
      authStatus: { onboardingCompleted: true },
    });
    expect(data.canViewRealtimeData).toBe(true);
  });

  it("withholds realtime data on a tenant host from a caller without glucose read", async () => {
    const data = await loadedData({
      host: `acme.${BASE}`,
      permissions: ["treatments.read"],
      status: { status: "ok", tenantSlug: "acme" },
      authStatus: { onboardingCompleted: true },
    });
    expect(data.canViewRealtimeData).toBe(false);
  });
});
