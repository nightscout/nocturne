import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { DataQualitySettings } from "#lib/api/generated/nocturne-api-client.js";

const { patchDataQualitySettings, refresh } = vi.hoisted(() => ({
  patchDataQualitySettings: vi.fn(),
  refresh: vi.fn(),
}));

const stored: DataQualitySettings = {
  sleepSchedule: { bedtimeHour: 23, wakeTimeHour: 7, timezone: "Pacific/Auckland" },
  compressionLowDetection: { enabled: true, excludeFromStatistics: true },
};

vi.mock("#lib/api/ui-settings.remote.js", () => ({
  getUiSettings: () => ({
    current: { dataQuality: stored },
    loading: false,
    error: undefined,
    refresh,
  }),
}));

vi.mock("#lib/api/generated/uiSettings.generated.remote.js", () => ({
  patchDataQualitySettings,
}));

import DataQualityPage from "./+page.svelte";

describe("settings/data-quality page", () => {
  beforeEach(() => {
    patchDataQualitySettings.mockReset();
    patchDataQualitySettings.mockResolvedValue(stored);
    refresh.mockReset();
    refresh.mockResolvedValue(undefined);
  });

  it("sends only the wake time when the wake time changes", async () => {
    render(DataQualityPage, {});

    await page.getByText("7:00 AM").click();
    await page.getByRole("option", { name: "8:00 AM" }).click();

    await expect.poll(() => patchDataQualitySettings.mock.calls.length).toBe(1);
    expect(patchDataQualitySettings).toHaveBeenCalledWith({ sleepSchedule: { wakeTimeHour: 8 } });
  });

  it("sends only the detection flag when detection is switched off", async () => {
    render(DataQualityPage, {});

    await page.getByRole("switch").click();

    await expect.poll(() => patchDataQualitySettings.mock.calls.length).toBe(1);
    expect(patchDataQualitySettings).toHaveBeenCalledWith({
      compressionLowDetection: { enabled: false },
    });
  });
});
