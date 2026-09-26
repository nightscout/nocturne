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
  /** Whether the API reports the caller as limited to the last 24 hours. */
  reportedLimit?: boolean;
  /** Scopes the auth handler already resolved, for a signed-in member. */
  resolved?: string[];
  /** The history limit the auth handler already resolved, for a signed-in member. */
  resolvedLimit?: boolean;
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
  limitTo24Hours: boolean;
  displayPreferences: UserDisplayPreferences[];
  serverPreferences: UserDisplayPreferences | null;
};

function runLoad(situation: Situation) {
  const getMyPermissions = vi.fn(async () => {
    if (situation.reported instanceof Error) throw situation.reported;
    return { scopes: situation.reported ?? [], limitTo24Hours: situation.reportedLimit ?? false };
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
    limitTo24Hours: situation.resolvedLimit,
    apiClient: {
      status: { getStatus: async () => ({ tenantSlug: null }) },
      myPermissions: { getMyPermissions },
      shareAppearance: { getShareAppearance },
    },
  };

  const cookies = { get: () => undefined } as unknown as Cookies;

  const event = {
    locals,
    cookies,
    request: new Request(`https://${situation.host}/`, {
      headers: { host: situation.host },
    }),
  };

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

  it("carries a share's 24-hour limit, which the API decides", async () => {
    const { data } = runLoad({
      host: SHARE_HOST,
      isShareHost: true,
      reported: ["glucose.read"],
      reportedLimit: true,
    });

    await expect(data).resolves.toMatchObject({ limitTo24Hours: true });
  });

  it("carries the 24-hour limit the auth handler resolved for a member", async () => {
    const { data } = runLoad({ host: TENANT_HOST, resolved: ["glucose.read"], resolvedLimit: true });

    await expect(data).resolves.toMatchObject({ limitTo24Hours: true });
  });

  it("does not limit a viewer the API reports as unlimited", async () => {
    const { data } = runLoad({
      host: SHARE_HOST,
      isShareHost: true,
      reported: ["glucose.read"],
      reportedLimit: false,
    });

    await expect(data).resolves.toMatchObject({ limitTo24Hours: false });
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

  it("asks for no owner's appearance on behalf of an anonymous tenant-host visitor", async () => {
    const { data, getShareAppearance } = runLoad({
      host: TENANT_HOST,
      ownerAppearance: { glucoseUnits: "mmol" },
    });

    await expect(data).resolves.toMatchObject({ serverPreferences: null });
    expect(getShareAppearance).not.toHaveBeenCalled();
  });
});
