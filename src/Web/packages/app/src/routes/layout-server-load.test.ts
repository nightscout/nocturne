import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Cookies } from "@sveltejs/kit";
import type { UserDisplayPreferences } from "$lib/api";
import { load } from "./+layout.server";

/**
 * The root layout's load, exercised for the two decisions it makes on its own: which viewers get
 * their granted scopes resolved, and which saved preferences the page is drawn with. The UI
 * offers surfaces from those scopes, so a viewer resolved to nothing silently loses navigation
 * while every unit test around the filters stays green; and a share viewer left without the
 * owner's preferences reads their glucose in units the owner never chose.
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
  /** The share link owner's presentation settings; a thrown value stands for a refused call. */
  ownerAppearance?: UserDisplayPreferences | Error;
  /** A signed-in member's own saved preferences. */
  memberPreferences?: UserDisplayPreferences;
}

/** The page data the load returned. */
type LoadedData = {
  effectivePermissions: string[];
  displayPreferences: UserDisplayPreferences[];
  serverPreferences: UserDisplayPreferences | null;
};

function runLoad(situation: Situation) {
  const getMyPermissions = vi.fn(async () => {
    if (situation.reported instanceof Error) throw situation.reported;
    return situation.reported ?? [];
  });
  const getShareAppearance = vi.fn(async () => {
    if (situation.ownerAppearance instanceof Error) throw situation.ownerAppearance;
    return situation.ownerAppearance ?? {};
  });

  const locals = {
    user: situation.memberPreferences ? { preferences: situation.memberPreferences } : null,
    isAuthenticated: situation.memberPreferences !== undefined,
    isPlatformAdmin: false,
    isShareHost: situation.isShareHost ?? false,
    isGuestSession: situation.isGuestSession ?? false,
    effectivePermissions: situation.resolved,
    apiClient: {
      status: { getStatus: async () => ({ tenantSlug: null }) },
      myPermissions: { getMyPermissions },
      shareAppearance: { getShareAppearance },
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
  return { data, getMyPermissions, getShareAppearance };
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

  it("draws a share host with the link owner's units and clock", async () => {
    const { data } = runLoad({
      host: SHARE_HOST,
      isShareHost: true,
      ownerAppearance: { glucoseUnits: "mmol", timeFormat: "24", colorTheme: "trio" },
    });

    const loaded = await data;
    expect(loaded.displayPreferences[0]).toMatchObject({
      glucoseUnits: "mmol",
      timeFormat: "24",
      colorTheme: "trio",
    });
    // The client hydrates from this, so SSR and the browser have to agree on one source.
    expect(loaded.serverPreferences).toMatchObject({ glucoseUnits: "mmol" });
  });

  it("leaves a share on the frontend defaults when the appearance call is refused", async () => {
    const { data } = runLoad({
      host: SHARE_HOST,
      isShareHost: true,
      ownerAppearance: new Error("404"),
    });

    const loaded = await data;
    expect(loaded.displayPreferences).toEqual([]);
    expect(loaded.serverPreferences).toBeNull();
  });

  it("never asks for a share owner's appearance on a tenant host", async () => {
    const { data, getShareAppearance } = runLoad({
      host: TENANT_HOST,
      memberPreferences: { glucoseUnits: "mg/dl" },
    });

    await expect(data).resolves.toMatchObject({
      serverPreferences: { glucoseUnits: "mg/dl" },
    });
    expect(getShareAppearance).not.toHaveBeenCalled();
  });
});
