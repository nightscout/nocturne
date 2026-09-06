import { describe, expect, it } from "vitest";
import { isRedirect, type Cookies } from "@sveltejs/kit";
import { classifyHost, isTenantlessHost } from "$lib/server/tenantless-host";
import { SHARE_UNAVAILABLE_PATH } from "$lib/share-host";
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
  /** The /api/v4/status document; a number rejects with that HTTP status. */
  status:
    | { status?: string; tenantSlug?: string | null; anonymousReadAccess?: boolean }
    | number;
  /** The passkey auth-status answer; a number rejects with that HTTP status. */
  authStatus: { onboardingCompleted?: boolean } | number;
  signedIn?: boolean;
  pathname?: string;
  /** The caller's effective permissions, as /api/v4/me/permissions reports them. */
  permissions?: string[];
  /** The cookies the browser presents on this host. */
  cookies?: Record<string, string>;
}

function runLoad(situation: Situation) {
  const { kind } = classifyHost(situation.host, BASE, situation.dashboardSlugs ?? []);
  const tenantless = isTenantlessHost(kind, situation.apexResolvesTenant ?? false);
  const shareHost = kind === "share";

  const jar = new Map(Object.entries(situation.cookies ?? {}));
  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- a stub of the three Cookies methods this load touches; implementing the full interface would say nothing
  const cookies = {
    get: (name: string) => jar.get(name),
    set: () => {},
    delete: () => {},
  } as unknown as Cookies;

  // A share host is never authenticated whatever the browser presents: the auth handler leaves
  // its cookies unread (hooks.server.ts, authHandle), so the owner of the data behind the link
  // arrives on it as anonymously as a stranger does.
  const signedIn = !shareHost && (situation.signedIn ?? true);
  const locals = {
    isGuestSession: false,
    isShareHost: shareHost,
    isAuthenticated: signedIn,
    user: signedIn ? { subjectId: "s1", name: "Sam" } : null,
    effectivePermissions: situation.permissions ?? ["*"],
    apiClient: {
      status: {
        getStatus: async () => {
          if (typeof situation.status === "number") throw { status: situation.status };
          return situation.status;
        },
      },
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
async function loadedData(
  situation: Situation
): Promise<{ canViewRealtimeData: boolean; user: { name: string } | null }> {
  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- the load's declared return includes void, for the paths that throw a redirect; these situations render
  return (await runLoad(situation)) as {
    canViewRealtimeData: boolean;
    user: { name: string } | null;
  };
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
        pathname: "/settings/members",
        ...populatedTenantless,
      })
    ).resolves.toBe("/");
  });

  it("leaves a subject-scoped route alone on a tenantless host", async () => {
    await expect(
      redirectLocation({
        host: BASE,
        pathname: "/settings/account",
        ...populatedTenantless,
      })
    ).resolves.toBeNull();
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

describe("(authenticated) layout load — the public share host", () => {
  const SHARE = `k7m2q9x4r3wt.share.${BASE}`;

  /** The tenant behind a live share link: set up, resolved, and granting anonymous read. */
  const sharedTenant = {
    status: { status: "ok", tenantSlug: "acme", anonymousReadAccess: true },
    authStatus: { onboardingCompleted: true },
  } as const;

  /** What the owner's browser carries on every host under the base domain after signing in. */
  const ownerSession = {
    IsAuthenticated: "true",
    nocturne_access_token: "owner-access",
    nocturne_refresh_token: "owner-refresh",
  };

  it("renders the shared view for a browser carrying the owner's session", async () => {
    await expect(
      redirectLocation({ host: SHARE, cookies: ownerSession, ...sharedTenant })
    ).resolves.toBeNull();
  });

  it("gives the owner's browser the same anonymous view a stranger's gets", async () => {
    const stranger = await loadedData({ host: SHARE, ...sharedTenant });
    const owner = await loadedData({ host: SHARE, cookies: ownerSession, ...sharedTenant });

    expect(owner).toEqual(stranger);
    expect(owner.user).toBeNull();
    expect(owner.canViewRealtimeData).toBe(true);
  });

  it("renders the shared view when the instance reports onboarding incomplete", async () => {
    // /setup is a sign-in destination for anyone without a session, so a share host sent there
    // lands on /auth/login by a longer road.
    await expect(
      redirectLocation({
        host: SHARE,
        cookies: ownerSession,
        status: { status: "ok", tenantSlug: "acme", anonymousReadAccess: true },
        authStatus: { onboardingCompleted: false },
      })
    ).resolves.toBeNull();
  });

  it("renders the shared view when the status document says setup_required", async () => {
    await expect(
      redirectLocation({
        host: SHARE,
        status: { status: "setup_required", tenantSlug: "acme", anonymousReadAccess: true },
        authStatus: { onboardingCompleted: true },
      })
    ).resolves.toBeNull();
  });

  it("tells a share host of a tenant that grants no anonymous read that the link is gone", async () => {
    // Sign-in is not an option the visitor has: the host holds no session, and the account the
    // login page would take belongs to a different host anyway.
    await expect(
      redirectLocation({
        host: SHARE,
        cookies: ownerSession,
        status: { status: "ok", tenantSlug: "acme", anonymousReadAccess: false },
        authStatus: { onboardingCompleted: true },
      })
    ).resolves.toBe(SHARE_UNAVAILABLE_PATH);
  });

  it("never serves the first-run wizard to a share host whose token resolves nothing", async () => {
    // A token the API cannot parse as one leaves the host resolving no tenant, and the status
    // endpoint reports "setup_required" for any request that resolves none — which put the
    // "WELCOME TO NOCTURNE" wizard in front of whoever held a rotated link.
    await expect(
      redirectLocation({
        host: SHARE,
        status: { status: "setup_required", tenantSlug: null, anonymousReadAccess: false },
        authStatus: 404,
      })
    ).resolves.toBe(SHARE_UNAVAILABLE_PATH);
  });

  it("does not claim a cause when the status call is what failed", async () => {
    // getRequestStatus swallows any failure to null, so a live link during an API blip is
    // indistinguishable here from one that was rotated. It lands on the same page, which is why
    // that page says the link is not working rather than that it was replaced — telling a viewer
    // to ask for a replacement would have the owner rotate, killing the link for everyone else.
    await expect(
      redirectLocation({ host: SHARE, status: 503, authStatus: 404 })
    ).resolves.toBe(SHARE_UNAVAILABLE_PATH);
  });

  it("never serves the first-run wizard to a share host reporting onboarding incomplete", async () => {
    await expect(
      redirectLocation({
        host: SHARE,
        status: { status: "ok", tenantSlug: "acme", anonymousReadAccess: false },
        authStatus: { onboardingCompleted: false },
      })
    ).resolves.toBe(SHARE_UNAVAILABLE_PATH);
  });

  it("keeps the bare tenant host login-only even when the tenant shares publicly", async () => {
    await expect(
      redirectLocation({ host: `acme.${BASE}`, signedIn: false, ...sharedTenant })
    ).resolves.toBe("/auth/login?returnUrl=%2F");
  });

  it("renders the tenant app for the owner on the bare tenant host", async () => {
    await expect(
      redirectLocation({ host: `acme.${BASE}`, cookies: ownerSession, ...sharedTenant })
    ).resolves.toBeNull();
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
