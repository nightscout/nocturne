import { render } from "vitest-browser-svelte";
import { describe, expect, it, vi } from "vitest";

const now = Date.UTC(2026, 5, 14, 9, 30, 0);

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => ({
    currentBG: 123,
    currentEntry: { mills: now, sgv: 123 },
    bgDelta: 4,
    lastUpdated: now,
    now,
    isConnected: true,
    entries: [{ mills: now, sgv: 123 }],
    direction: "Flat",
    demoMode: false,
    pillsData: { cob: null, basal: null, iob: null, loop: null },
    trackerInstances: [],
    trackerDefinitions: [],
    timeSinceReading: Symbol("derived sentinel"),
  }),
}));

vi.mock("$lib/stores/settings-store.svelte", () => ({
  getSettingsStore: () => ({
    features: { trackerPills: { enabled: false } },
  }),
}));

import CurrentBGDisplay from "./CurrentBGDisplay.svelte";

describe("CurrentBGDisplay", () => {
  it("renders status text without reading a Symbol timeSinceReading value", () => {
    expect(() => render(CurrentBGDisplay, { showPills: false })).not.toThrow();
  });
});
