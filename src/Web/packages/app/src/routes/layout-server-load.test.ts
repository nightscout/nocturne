import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Cookies } from "@sveltejs/kit";
import { load } from "./+layout.server";

/**
 * The root layout's load, exercised for the one decision it makes on its own: which viewers get
 * their granted scopes resolved. The UI offers surfaces from those scopes, so a viewer resolved
 * to nothing silently loses navigation while every unit test around the filters stays green.
 */

type LoadEvent = Parameters<typeof load>[0];

interface Situation {
  host: string;
  /** Scopes the API reports for the caller; a thrown value stands for a refused call. */
  reported?: string[] | Error;
  /** Scopes the auth handler already resolved, for a signed-in member. */
  resolved?: string[];
  isShareHost?: boolean;
  isGuestSession?: boolean;
}

/** The page data the load returned. */
type LoadedData = { effectivePermissions: string[] };

function runLoad(situation: Situation) {
  const getMyPermissions = vi.fn(async () => {
    if (situation.reported instanceof Error) throw situation.reported;
    return situation.reported ?? [];
  });

  const locals = {
    user: null,
    isAuthenticated: false,
    isPlatformAdmin: false,
    isShareHost: situation.isShareHost ?? false,
    isGuestSession: situation.isGuestSession ?? false,
    effectivePermissions: situation.resolved,
    apiClient: {
      status: { getStatus: async () => ({ tenantSlug: null }) },
      myPermissions: { getMyPermissions },
    },
  };

  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- a stub of the one Cookies method this load touches; implementing the full interface would say nothing
  const cookies = { get: () => undefined } as unknown as Cookies;

  const event = {
    locals,
    cookies,
    request: new Request(`https://${situation.host}/`, {
      headers: { host: situation.host },
    }),
  };

  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- the load reads three fields of the request event; the rest of SvelteKit's ServerLoadEvent is not reachable from here
  const data = load(event as unknown as LoadEvent) as unknown as Promise<LoadedData>;
  return { data, getMyPermissions };
}

async function permissions(situation: Situation): Promise<string[]> {
  return (await runLoad(situation).data).effectivePermissions;
}

const SHARE_HOST = "k7m2q9x4r3wt.share.nocturne.run";
const TENANT_HOST = "rhys.nocturne.run";

describe("root layout load", () => {
  beforeEach(() => {
    process.env.BASE_DOMAIN = "nocturne.run";
  });
  afterEach(() => {
    delete process.env.BASE_DOMAIN;
  });

  it("resolves a public share's own grant, which no auth handler resolved for it", async () => {
    await expect(
      permissions({
        host: SHARE_HOST,
        isShareHost: true,
        reported: ["glucose.read", "reports.read"],
      })
    ).resolves.toEqual(["glucose.read", "reports.read"]);
  });

  it("resolves a guest link's grant too", async () => {
    await expect(
      permissions({
        host: TENANT_HOST,
        isGuestSession: true,
        reported: ["glucose.read"],
      })
    ).resolves.toEqual(["glucose.read"]);
  });

  it("leaves a share with nothing when the call is refused", async () => {
    await expect(
      permissions({
        host: SHARE_HOST,
        isShareHost: true,
        reported: new Error("403"),
      })
    ).resolves.toEqual([]);
  });

  it("keeps the grant the auth handler already resolved for a member", async () => {
    const { data, getMyPermissions } = runLoad({
      host: TENANT_HOST,
      resolved: ["*"],
      reported: ["glucose.read"],
    });

    await expect(data).resolves.toMatchObject({ effectivePermissions: ["*"] });
    expect(getMyPermissions).not.toHaveBeenCalled();
  });

  it("asks nothing on behalf of an unresolved anonymous visitor", async () => {
    const { data, getMyPermissions } = runLoad({
      host: TENANT_HOST,
      reported: ["glucose.read"],
    });

    await expect(data).resolves.toMatchObject({ effectivePermissions: [] });
    expect(getMyPermissions).not.toHaveBeenCalled();
  });
});
