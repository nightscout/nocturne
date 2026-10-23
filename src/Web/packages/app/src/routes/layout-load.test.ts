import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: true, building: false, dev: false }));

const registerPreferenceCookieDomain = vi.fn();
const registerPreferencesWriteThrough = vi.fn();
const reconcilePreferences = vi.fn();
const applyTenantPresentation = vi.fn();
const presentOwnerPreferences = vi.fn();

vi.mock("$lib/stores/appearance-store.svelte", () => ({
  registerPreferenceCookieDomain: (...args: unknown[]) =>
    registerPreferenceCookieDomain(...args),
  registerPreferencesWriteThrough: (...args: unknown[]) =>
    registerPreferencesWriteThrough(...args),
  reconcilePreferences: (...args: unknown[]) => reconcilePreferences(...args),
  applyTenantPresentation: (...args: unknown[]) => applyTenantPresentation(...args),
  presentOwnerPreferences: (...args: unknown[]) => presentOwnerPreferences(...args),
  preferredLanguage: { current: "en" },
  isSupportedLocale: (locale: string) => locale === "en",
  setLanguage: vi.fn(),
}));

vi.mock("$lib/api/user-preferences.remote", () => ({ updateDisplayPreferences: vi.fn() }));

// The real loaders read catalogs that only a wuchale build compiles.
vi.mock("wuchale/load-utils", () => ({ loadLocale: vi.fn() }));
vi.mock("../../../../locales/main.loader.svelte.js", () => ({}));
vi.mock("../../../../locales/js.loader.js", () => ({}));

const { load } = await import("./+layout");

type LoadEvent = Parameters<typeof load>[0];

function runLoad(data: Record<string, unknown>) {
  return load({
    url: new URL("https://example.test/"),
    data,
  } as unknown as LoadEvent);
}

/**
 * The universal layout load's wiring of the preference store. A share host is a sibling of every
 * tenant host under one base domain, so what it is allowed to persist is the whole question.
 */
describe("root layout universal load", () => {
  beforeEach(() => {
    registerPreferenceCookieDomain.mockClear();
    registerPreferencesWriteThrough.mockClear();
    reconcilePreferences.mockClear();
    applyTenantPresentation.mockClear();
    presentOwnerPreferences.mockClear();
  });

  it("draws a share viewer with the link owner's preferences, not reconciling them as the viewer's", async () => {
    await runLoad({
      isShareHost: true,
      isAuthenticated: false,
      baseDomain: "nocturne.run",
      serverPreferences: { glucoseUnits: "mmol" },
    });

    expect(presentOwnerPreferences).toHaveBeenCalledWith({ glucoseUnits: "mmol" });
    expect(reconcilePreferences).not.toHaveBeenCalled();
  });

  it("never widens the preference cookie or writes back from a share host", async () => {
    await runLoad({
      isShareHost: true,
      isAuthenticated: false,
      baseDomain: "nocturne.run",
      serverPreferences: { glucoseUnits: "mmol" },
    });

    expect(registerPreferenceCookieDomain).not.toHaveBeenCalled();
    expect(registerPreferencesWriteThrough).not.toHaveBeenCalledWith(expect.any(Function));
  });

  it("keeps both registered for a signed-in member", async () => {
    await runLoad({
      isShareHost: false,
      isAuthenticated: true,
      hasAccount: true,
      baseDomain: "nocturne.run",
      user: { preferences: { glucoseUnits: "mg/dl" } },
      serverPreferences: { glucoseUnits: "mg/dl" },
    });

    expect(registerPreferenceCookieDomain).toHaveBeenCalledWith("nocturne.run");
    expect(registerPreferencesWriteThrough).toHaveBeenCalledWith(expect.any(Function));
    expect(reconcilePreferences).toHaveBeenCalledWith({ glucoseUnits: "mg/dl" });
  });

  it.each([
    ["a guest", { isAuthenticated: true, hasAccount: false }],
    ["an anonymous visitor", { isAuthenticated: false, hasAccount: false }],
  ])("withdraws the write-through and never reconciles for %s, but still sets the cookie domain", async (_, viewer) => {
    await runLoad({
      isShareHost: false,
      baseDomain: "nocturne.run",
      serverPreferences: null,
      ...viewer,
    });

    expect(registerPreferenceCookieDomain).toHaveBeenCalledWith("nocturne.run");
    expect(registerPreferencesWriteThrough).toHaveBeenCalledTimes(1);
    expect(registerPreferencesWriteThrough).toHaveBeenCalledWith(null);
    expect(reconcilePreferences).not.toHaveBeenCalled();
  });

  it("leaves nothing registered when a member's layout reloads as a guest", async () => {
    const base = { isShareHost: false, baseDomain: "nocturne.run", serverPreferences: null };
    await runLoad({ ...base, isAuthenticated: true, hasAccount: true });
    await runLoad({ ...base, isAuthenticated: true, hasAccount: false });

    expect(registerPreferencesWriteThrough.mock.calls.map(([fn]) => typeof fn)).toEqual([
      "function",
      "object",
    ]);
    expect(registerPreferencesWriteThrough.mock.lastCall?.[0]).toBeNull();
  });

  it("falls a guest back to the tenant's presentation", async () => {
    await runLoad({
      isShareHost: false,
      isAuthenticated: true,
      hasAccount: false,
      baseDomain: "nocturne.run",
      serverPreferences: null,
      tenantPreferences: { glucoseUnits: "mmol" },
    });

    expect(applyTenantPresentation).toHaveBeenCalledWith({ glucoseUnits: "mmol" });
  });
});
