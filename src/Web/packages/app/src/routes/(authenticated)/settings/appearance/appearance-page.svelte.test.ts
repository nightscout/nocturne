import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { FeatureSettings } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery, remoteQueryError } from "#lib/test-stubs/remote-resource.js";
import { page as appState } from "$app/state";
import { missingScopeError } from "#lib/forms/submit-messages.svelte.js";

const { patchFeatureSettings, refresh, setPreference, sourceDefaults, toastError } = vi.hoisted(() => ({
  patchFeatureSettings: vi.fn(),
  sourceDefaults: { unread: false },
  refresh: vi.fn(),
  setPreference: vi.fn(),
  toastError: vi.fn(),
}));

vi.mock("svelte-sonner", () => ({
  toast: { error: toastError, success: vi.fn() },
}));

const stored: FeatureSettings = {
  display: { focusHours: 6, nightMode: true, showRawBG: true },
  trackerPills: { enabled: true },
};

vi.mock("#lib/api/ui-settings.remote.js", () => ({
  getUiSettings: () => ({
    current: { features: stored },
    loading: false,
    error: undefined,
    refresh,
  }),
}));

vi.mock("#lib/api/generated/uiSettings.generated.remote.js", () => ({
  patchFeatureSettings,
}));

vi.mock("#lib/api/generated/glucoseProcessingSettings.generated.remote.js", () => ({
  getPreference: () => remoteQuery(() => ({ preferredGlucoseProcessing: null })),
  getSourceDefaults: () =>
    sourceDefaults.unread
      ? remoteQueryError(new Error("unavailable"))
      : remoteQuery(() => ({ rules: [] })),
  setPreference,
  setSourceDefaults: vi.fn(),
}));

vi.mock("#lib/api/user-preferences.remote.js", () => ({
  updateLanguagePreference: vi.fn(),
}));

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => ({ now: 0 }),
  tryGetRealtimeStore: () => ({ now: 0 }),
}));

import AppearancePage from "./+page.svelte";

describe("settings/appearance page", () => {
  beforeEach(() => {
    patchFeatureSettings.mockReset();
    patchFeatureSettings.mockResolvedValue(stored);
    refresh.mockReset();
    refresh.mockResolvedValue(undefined);
    setPreference.mockReset();
    setPreference.mockResolvedValue(undefined);
    toastError.mockReset();
    sourceDefaults.unread = false;
    appState.data = { effectivePermissions: ["tenant.settings"], refusedAsDemoSubject: false };
  });

  const displayPreference = () =>
    page
      .getByText("Display preference", { exact: true })
      .element()
      .parentElement
      ?.querySelector<HTMLButtonElement>("[data-slot=select-trigger]");

  const notice = "Only members who can manage this site's settings can change glucose processing.";

  it("lets a member holding tenant.settings change glucose processing", async () => {
    render(AppearancePage, {});

    expect(displayPreference()?.disabled).toBe(false);
    await expect.element(page.getByRole("button", { name: "Configure" })).toBeEnabled();
    expect(document.body.textContent).not.toContain(notice);
  });

  it("keeps Configure closed until the stored source rules are read", async () => {
    sourceDefaults.unread = true;

    render(AppearancePage);

    await expect.element(page.getByRole("button", { name: "Configure" })).toBeDisabled();
  });

  it("shows a member without tenant.settings the setting, disabled, with the reason", async () => {
    appState.data = { effectivePermissions: ["glucose.read"], refusedAsDemoSubject: false };
    render(AppearancePage, {});

    expect(displayPreference()?.disabled).toBe(true);
    await expect.element(page.getByRole("button", { name: "Configure" })).toBeDisabled();
    expect(document.body.textContent).toContain(notice);
  });

  it("disables glucose processing for the demo visitor, who holds tenant.settings", async () => {
    appState.data = { effectivePermissions: ["tenant.settings"], refusedAsDemoSubject: true };
    render(AppearancePage, {});

    expect(displayPreference()?.disabled).toBe(true);
    await expect.element(page.getByRole("button", { name: "Configure" })).toBeDisabled();
    expect(document.body.textContent).toContain(notice);
  });

  it("tells the user a refused save was refused, without naming the scope, and the list returns to the stored value", async () => {
    setPreference.mockRejectedValue({
      status: 403,
      body: {
        message: "This operation requires the 'tenant.settings' scope.",
        code: "missing_scope",
      },
    });
    render(AppearancePage, {});

    await page.elementLocator(displayPreference()!).click();
    await page.getByRole("option", { name: "Smoothed", exact: true }).click();

    await expect.poll(() => toastError.mock.calls.length).toBe(1);
    expect(toastError).toHaveBeenCalledWith(missingScopeError());

    await page.elementLocator(displayPreference()!).click();
    await expect
      .element(page.getByRole("option", { name: "Default", exact: true }))
      .toHaveAttribute("aria-selected", "true");
    await expect
      .element(page.getByRole("option", { name: "Smoothed", exact: true }))
      .not.toHaveAttribute("aria-selected", "true");

    await page.getByRole("option", { name: "Smoothed", exact: true }).click();
    await expect.poll(() => setPreference.mock.calls.length).toBe(2);
  });

  it("sends only the focus hours when the default chart range changes", async () => {
    render(AppearancePage, {});

    await page.getByText("6 hours", { exact: true }).click();
    await page.getByRole("option", { name: "12 hours" }).click();

    await expect.poll(() => patchFeatureSettings.mock.calls.length).toBe(1);
    expect(patchFeatureSettings).toHaveBeenCalledWith({ display: { focusHours: 12 } });
  });

  it("sends only the enabled flag when tracker pills are switched off", async () => {
    render(AppearancePage, {});

    const label = page.getByText("Show tracker pills", { exact: true }).element();
    label.closest(".justify-between")?.querySelector<HTMLButtonElement>("[role=switch]")?.click();

    await expect.poll(() => patchFeatureSettings.mock.calls.length).toBe(1);
    expect(patchFeatureSettings).toHaveBeenCalledWith({ trackerPills: { enabled: false } });
  });
});
