import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: true, building: false, dev: false }));

const registerPreferenceCookieDomain = vi.fn();
const registerPreferencesWriteThrough = vi.fn();
const reconcilePreferences = vi.fn();

vi.mock("$lib/stores/appearance-store.svelte", () => ({
  registerPreferenceCookieDomain: (...args: unknown[]) =>
    registerPreferenceCookieDomain(...args),
  registerPreferencesWriteThrough: (...args: unknown[]) =>
    registerPreferencesWriteThrough(...args),
  reconcilePreferences: (...args: unknown[]) => reconcilePreferences(...args),
  preferredLanguage: { current: "en" },
  isSupportedLocale: (locale: string) => locale === "en",
  setLanguage: vi.fn(),
}));

vi.mock("$lib/api/user-preferences.remote", () => ({ updateDisplayPreferences: vi.fn() }));

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
  });

  it("hydrates a share viewer from the link owner's preferences", async () => {
    await runLoad({
      isShareHost: true,
      isAuthenticated: false,
      baseDomain: "nocturne.run",
      serverPreferences: { glucoseUnits: "mmol" },
    });

    expect(reconcilePreferences).toHaveBeenCalledWith({ glucoseUnits: "mmol" });
  });

  it("never widens the preference cookie or writes back from a share host", async () => {
    await runLoad({
      isShareHost: true,
      isAuthenticated: false,
      baseDomain: "nocturne.run",
      serverPreferences: { glucoseUnits: "mmol" },
    });

    expect(registerPreferenceCookieDomain).not.toHaveBeenCalled();
    expect(registerPreferencesWriteThrough).not.toHaveBeenCalled();
  });

  it("keeps both registered for a signed-in member", async () => {
    await runLoad({
      isShareHost: false,
      isAuthenticated: true,
      baseDomain: "nocturne.run",
      user: { preferences: { glucoseUnits: "mg/dl" } },
      serverPreferences: { glucoseUnits: "mg/dl" },
    });

    expect(registerPreferenceCookieDomain).toHaveBeenCalledWith("nocturne.run");
    expect(registerPreferencesWriteThrough).toHaveBeenCalled();
    expect(reconcilePreferences).toHaveBeenCalledWith({ glucoseUnits: "mg/dl" });
  });
});
