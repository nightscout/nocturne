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

const battery = vi.hoisted(() => ({ status: undefined as unknown }));

vi.mock("$api/generated/batteries.generated.remote", () => ({
  getCurrentBatteryStatus: () => Promise.resolve(battery.status),
}));

// Neutral keeps the watercolour engine out of the test.
vi.mock("$lib/stores/current-glucose-status.svelte", () => ({
  currentGlucoseStatus: () => undefined,
}));

import { setGlucoseUnits } from "$lib/stores/appearance-store.svelte";
import CurrentGlucoseWidget from "./CurrentGlucoseWidget.svelte";

const tile = () => page.getByTestId("current-glucose-tile");

describe("CurrentGlucoseWidget", () => {
  beforeEach(() => {
    store.currentEntry.mills = now - 2 * 60_000;
    store.lastUpdated = now - 2 * 60_000;
    store.now = now;
    battery.status = undefined;
    setGlucoseUnits("mg/dl");
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

  it("shows the uploader battery beside the change", async () => {
    battery.status = {
      level: 62,
      display: "62%",
      status: "ok",
      devices: { uploader: {} },
      min: { isCharging: false },
    };
    render(CurrentGlucoseWidget);

    await expect.element(page.getByText("62%", { exact: true })).toBeVisible();
  });

  it("says a stale reading is stale to a screen reader", async () => {
    store.lastUpdated = now - 30 * 60_000;
    render(CurrentGlucoseWidget);

    await expect.element(page.getByText("Stale reading")).toBeInTheDocument();
  });

  it.each(["mg/dl", "mmol"] as const)("keeps the %s tile inside a narrow cell", async (units) => {
    setGlucoseUnits(units);
    store.currentBG = units === "mmol" ? 222 : 123;
    for (const width of [155, 200, 240, 320, 440]) {
      const { container, unmount } = render(CurrentGlucoseWidget);
      container.style.width = `${width}px`;
      await expect.element(tile()).toBeVisible();

      const value = tile().element().querySelector<HTMLElement>('[data-slot="glucose-value-indicator"] > div')!;
      const box = value.getBoundingClientRect();
      const cell = container.getBoundingClientRect();
      const label = `${units} ${width}px`;

      expect(value.scrollWidth, label).toBeLessThanOrEqual(value.clientWidth);
      expect(box.right, label).toBeLessThanOrEqual(cell.right);
      for (const child of value.querySelectorAll("*")) {
        expect(child.getBoundingClientRect().right, label).toBeLessThanOrEqual(box.right + 0.5);
      }
      unmount();
    }
    store.currentBG = 123;
  });
});
