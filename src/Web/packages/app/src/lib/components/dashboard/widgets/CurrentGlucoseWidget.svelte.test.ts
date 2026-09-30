import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const now = Date.UTC(2026, 5, 14, 9, 30, 0);

const store = vi.hoisted(() => ({
  currentBG: 123,
  currentEntry: { mills: 0 },
  bgDelta: 4,
  lastUpdated: 0,
  now: 0,
  connectionStatus: "connected",
  entries: [{ mills: 0, sgv: 123 }],
  direction: "Flat",
}));

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => store,
}));

// Neutral keeps the watercolour engine out of the test.
vi.mock("$lib/stores/current-glucose-status.svelte", () => ({
  currentGlucoseStatus: () => undefined,
}));

import CurrentGlucoseWidget from "./CurrentGlucoseWidget.svelte";

const tile = () => page.getByTestId("current-glucose-tile");

describe("CurrentGlucoseWidget", () => {
  beforeEach(() => {
    store.currentEntry.mills = now - 2 * 60_000;
    store.lastUpdated = now - 2 * 60_000;
    store.now = now;
  });

  it("shows the reading with its unit, trend, change and age", async () => {
    render(CurrentGlucoseWidget);

    await expect.element(tile()).toHaveTextContent(/^123\s*stable\s*mg\/dL$/);
    await expect.element(page.getByText("+4", { exact: true })).toBeVisible();
    await expect.element(page.getByText(/2 min/)).toBeVisible();
  });

  it("drops the trend and change of a stale reading but keeps its age", async () => {
    store.lastUpdated = now - 30 * 60_000;
    render(CurrentGlucoseWidget);

    await expect.element(page.getByText(/30 min/)).toBeVisible();
    await expect.element(tile()).toHaveTextContent(/^123\s*mg\/dL$/);
    await expect.element(page.getByText("+4", { exact: true })).not.toBeInTheDocument();
  });
});
