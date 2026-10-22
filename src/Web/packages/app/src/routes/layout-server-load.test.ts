import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Cookies } from "@sveltejs/kit";
import type { UserDisplayPreferences } from "$lib/api";
import { SHELL_DATA_KEY } from "$lib/constants/shell-data";
import { PREFS_COOKIE_NAME } from "$lib/stores/appearance-store.svelte";
import { load } from "./+layout.server";

/**
 * The root layout's load, exercised for the two decisions it makes on its own: which viewers get
 * their granted scopes resolved, and which saved preferences the page is drawn with. The UI
 * offers surfaces from those scopes, so a viewer resolved to nothing silently loses navigation
 * while every unit test around the filters stays green; and a share or guest viewer left without
 * the owner's preferences reads their glucose in units the owner never chose.
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
  /** The tenant owner's presentation settings; a thrown value stands for a refused call. */
  ownerAppearance?: UserDisplayPreferences | Error;
  /** A signed-in member's own saved preferences. */
  memberPreferences?: UserDisplayPreferences;
  /** What the viewer's own `nocturne-prefs` cookie holds. */
  prefsCookie?: UserDisplayPreferences;
}

/** The page data the load returned. */
type LoadedData = {
  effectivePermissions: string[];
  limitTo24Hours: boolean;
  displayPreferences: UserDisplayPreferences[];
  serverPreferences: UserDisplayPreferences | null;
  tenantPreferences: UserDisplayPreferences | null;
};

function runLoad(situation: Situation) {
  const getMyPermissions = vi.fn(async () => {
    if (situation.reported instanceof Error) throw situation.reported;
    return { scopes: situation.reported ?? [], limitTo24Hours: situation.reportedLimit ?? false };
  });
  const getTenantAppearance = vi.fn(async () => {
    if (situation.ownerAppearance instanceof Error) throw situation.ownerAppearance;
    return situation.ownerAppearance ?? {};
  });

  const locals = {
    user: situation.memberPreferences
      ? { preferences: situation.memberPreferences }
      : situation.isGuestSession
        ? { subjectId: "guest", name: "Guest" }
        : null,
    isAuthenticated: situation.memberPreferences !== undefined || situation.isGuestSession === true,
    isPlatformAdmin: false,
    isShareHost: situation.isShareHost ?? false,
    isGuestSession: situation.isGuestSession ?? false,
    effectivePermissions: situation.resolved,
    limitTo24Hours: situation.resolvedLimit,
    apiClient: {
      status: { getStatus: async () => ({ tenantSlug: null }) },
      myPermissions: { getMyPermissions },
      tenantAppearance: { getTenantAppearance },
    },
  };

  const cookies = {
    get: (name: string) =>
      name === PREFS_COOKIE_NAME && situation.prefsCookie
        ? encodeURIComponent(JSON.stringify(situation.prefsCookie))
        : undefined,
  } as unknown as Cookies;

  const depends = vi.fn();
  const event = {
    locals,
    cookies,
    depends,
    request: new Request(`https://${situation.host}/`, {
      headers: { host: situation.host },
    }),
  };

  const data = load(event as unknown as LoadEvent) as unknown as Promise<LoadedData>;
  return { data, getMyPermissions, getTenantAppearance, depends };
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

  it("registers the shell key, so a revalidation re-reads the grant and the viewer", async () => {
    const { data, depends } = runLoad({ host: TENANT_HOST, resolved: ["*"], memberPreferences: {} });
    await data;
    expect(depends).toHaveBeenCalledExactlyOnceWith(SHELL_DATA_KEY);
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

  it("keeps the owner's choices over a share viewer's own cookie", async () => {
    const { data } = runLoad({
      host: SHARE_HOST,
      isShareHost: true,
      ownerAppearance: { glucoseUnits: "mmol" },
      prefsCookie: { glucoseUnits: "mg/dl", timeFormat: "24" },
    });

    const loaded = await data;
    expect(loaded.displayPreferences).toEqual([{ glucoseUnits: "mmol" }]);
  });

  it("draws a field the share owner left unset with the default, not the viewer's cookie", async () => {
    const { data } = runLoad({
      host: SHARE_HOST,
      isShareHost: true,
      ownerAppearance: { colorTheme: "trio" },
      prefsCookie: { glucoseUnits: "mmol" },
    });

    const loaded = await data;
    expect(loaded.displayPreferences).toEqual([{ colorTheme: "trio" }]);
  });

  it("draws a guest with the tenant owner's units and clock when the guest set none", async () => {
    const { data, getTenantAppearance } = runLoad({
      host: TENANT_HOST,
      isGuestSession: true,
      ownerAppearance: { glucoseUnits: "mmol", timeFormat: "24" },
    });

    const loaded = await data;
    expect(getTenantAppearance).toHaveBeenCalledOnce();
    expect(loaded.displayPreferences).toEqual([{ glucoseUnits: "mmol", timeFormat: "24" }]);
    // Not the guest's own, so the client must not reconcile it over the guest's cookie.
    expect(loaded.serverPreferences).toBeNull();
    expect(loaded.tenantPreferences).toEqual({ glucoseUnits: "mmol", timeFormat: "24" });
  });

  it("lets a guest's own cookie win over the tenant owner's presentation, field by field", async () => {
    const { data } = runLoad({
      host: TENANT_HOST,
      isGuestSession: true,
      ownerAppearance: { glucoseUnits: "mmol", timeFormat: "24" },
      prefsCookie: { glucoseUnits: "mg/dl" },
    });

    const loaded = await data;
    expect(loaded.displayPreferences).toEqual([
      { glucoseUnits: "mg/dl" },
      { glucoseUnits: "mmol", timeFormat: "24" },
    ]);
  });

  it("leaves a guest on the frontend defaults when the appearance call is refused", async () => {
    const { data } = runLoad({
      host: TENANT_HOST,
      isGuestSession: true,
      ownerAppearance: new Error("404"),
    });

    const loaded = await data;
    expect(loaded.displayPreferences).toEqual([]);
    expect(loaded.tenantPreferences).toBeNull();
  });

  it("never asks for the owner's appearance on behalf of a signed-in member", async () => {
    const { data, getTenantAppearance } = runLoad({
      host: TENANT_HOST,
      memberPreferences: { glucoseUnits: "mg/dl" },
    });

    await expect(data).resolves.toMatchObject({
      serverPreferences: { glucoseUnits: "mg/dl" },
    });
    expect(getTenantAppearance).not.toHaveBeenCalled();
  });

  it("asks for no owner's appearance on behalf of an anonymous tenant-host visitor, who can read no data", async () => {
    const { data, getTenantAppearance } = runLoad({
      host: TENANT_HOST,
      ownerAppearance: { glucoseUnits: "mmol" },
    });

    await expect(data).resolves.toMatchObject({ serverPreferences: null });
    expect(getTenantAppearance).not.toHaveBeenCalled();
  });
});
