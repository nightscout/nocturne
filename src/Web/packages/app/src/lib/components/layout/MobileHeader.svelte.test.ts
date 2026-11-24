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
    connectionUnavailable: false,
  };
});

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));

import Harness from "./MobileHeaderHarness.test.svelte";

describe("MobileHeader", () => {
  beforeEach(async () => {
    // The header shows only below the md breakpoint.
    await page.viewport(400, 800);
    store.connectionUnavailable = false;
    store.currentBG = 123;
    store.currentEntry = { mills: store.now, sgv: 123 };
  });

  it("shows no reading, not 0 or a skeleton, when the newest reading has no value", async () => {
    store.currentBG = 0;
    store.currentEntry = { mills: store.now, sgv: 0 };

    render(Harness);

    await expect.element(page.getByText("No glucose reading")).toBeInTheDocument();
    await expect.element(page.getByText("—", { exact: true })).toBeVisible();
    expect(page.getByText("0", { exact: true }).elements()).toHaveLength(0);
  });

  it("shows the current reading", async () => {
    render(Harness);

    await expect.element(page.getByText("123")).toBeVisible();
    expect(page.getByText("Connection error").elements()).toHaveLength(0);
  });

  it("says the connection failed in place of the reading age", async () => {
    store.connectionUnavailable = true;

    render(Harness);

    await expect.element(page.getByText("Connection error")).toBeVisible();
  });
});
