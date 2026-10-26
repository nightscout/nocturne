import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const store = vi.hoisted(() => {
  const now = Date.now();
  return {
    currentBG: 123,
    currentEntry: { mills: now, sgv: 123 },
    bgDelta: 4,
    direction: "Flat",
    lastUpdated: now,
    now,
    timeSinceReading: "2m ago",
    connectionUnavailable: false,
  };
});

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));

import CommandPaletteVitals from "./CommandPaletteVitals.svelte";

describe("CommandPaletteVitals", () => {
  beforeEach(() => {
    store.currentBG = 123;
    store.connectionUnavailable = false;
  });

  it("shows the time since the last reading while connected", async () => {
    render(CommandPaletteVitals);

    await expect.element(page.getByText("123")).toBeVisible();
    await expect.element(page.getByText("2m ago")).toBeVisible();
  });

  it("says the connection failed in place of the reading age", async () => {
    store.connectionUnavailable = true;

    render(CommandPaletteVitals);

    await expect.element(page.getByText("Connection error")).toBeVisible();
    expect(page.getByText("2m ago").elements()).toHaveLength(0);
  });

  it("says there is no data before the first reading", async () => {
    store.currentBG = 0;

    render(CommandPaletteVitals);

    await expect.element(page.getByText("No data")).toBeVisible();
  });
});
