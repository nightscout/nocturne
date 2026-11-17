import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { FeatureSettings } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const { patchFeatureSettings, refresh } = vi.hoisted(() => ({
  patchFeatureSettings: vi.fn(),
  refresh: vi.fn(),
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
  getSourceDefaults: () => remoteQuery(() => ({ rules: [] })),
  setPreference: vi.fn(),
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
