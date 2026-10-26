import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { GlucoseStatus } from "#lib/api/generated/nocturne-api-client.js";
import { releaseWatercolour, resetWatercolour } from "#lib/test-stubs/watercolour-deferral.svelte.js";

const now = Date.UTC(2026, 5, 14, 9, 30, 0);

const mocked = vi.hoisted(() => ({
  store: undefined as unknown,
  summary: undefined as unknown,
}));

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => mocked.store,
}));

const battery = vi.hoisted(() => ({ status: undefined as unknown }));

vi.mock("#lib/api/generated/batteries.generated.remote.js", () => ({
  getCurrentBatteryStatus: () => Promise.resolve(battery.status),
}));

vi.mock("#lib/api/generated/summaries.generated.remote.js", () => ({
  getSummary: () => remoteQuery(() => mocked.summary),
}));

vi.mock("water-ui", async (importOriginal) => {
  const fake = await import("#lib/test-stubs/Watercolour.test-stub.svelte");
  return {
    ...(await importOriginal<typeof import("water-ui")>()),
    Artwork: fake.default,
    mountPlayer: fake.mountPlayer,
    bloomScene: fake.bloomScene,
  };
});

vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"));

const store = $state({
  currentBG: 123,
  currentEntry: { mills: 0 },
  bgDelta: 4,
  lastUpdated: 0,
  now: 0,
  connectionStatus: "connected",
  entries: [{ mills: 0, sgv: 123 }],
  direction: "Flat",
});
mocked.store = store;

// No current status keeps the tile neutral, so it paints no wash.
const summary = $state<{ current?: { mills: number; status: GlucoseStatus } }>({});
mocked.summary = summary;

import { setGlucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { illustrations } from "#lib/stores/illustrations.svelte.js";
import CurrentGlucoseWidget from "./CurrentGlucoseWidget.svelte";

const tile = () => page.getByTestId("current-glucose-tile");

describe("CurrentGlucoseWidget", () => {
  beforeEach(() => {
    store.currentBG = 123;
    store.currentEntry = { mills: now - 2 * 60_000 };
    store.lastUpdated = now - 2 * 60_000;
    store.now = now;
    battery.status = undefined;
    summary.current = undefined;
    setGlucoseUnits("mg/dl");
    releaseWatercolour();
  });

  // The units persist to localStorage, which later test files in the browser run share.
  afterEach(() => {
    illustrations.current = "animated";
    setGlucoseUnits("mg/dl");
  });

  it("lays the still wash over the range fill at once under the Still preference", async () => {
    resetWatercolour();
    illustrations.current = "still";
    summary.current = { mills: store.currentEntry.mills, status: GlucoseStatus.High };
    render(CurrentGlucoseWidget);

    await expect.element(page.getByTestId("artwork-wash")).toBeInTheDocument();
  });

  it("keeps the flat range fill with illustrations off", async () => {
    illustrations.current = "off";
    summary.current = { mills: store.currentEntry.mills, status: GlucoseStatus.High };
    render(CurrentGlucoseWidget);

    const indicator = () => tile().element().querySelector('[data-slot="glucose-value-indicator"]');
    await vi.waitFor(() => expect(indicator()?.innerHTML).toContain("bg-glucose-high"));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(page.getByTestId("artwork-wash").elements()).toHaveLength(0);
    expect(page.getByTestId("glucose-tile-bloom").elements()).toHaveLength(0);
  });

  it("lays the wash's still over the reading on screen at once, and blooms the next reading once the engine window is over", async () => {
    resetWatercolour();
    summary.current = { mills: store.currentEntry.mills, status: GlucoseStatus.High };
    render(CurrentGlucoseWidget);

    await expect.element(page.getByTestId("artwork-wash")).toBeInTheDocument();
    expect(page.getByTestId("glucose-tile-bloom").elements()).toHaveLength(0);

    releaseWatercolour();
    const next = store.currentEntry.mills + 5 * 60_000;
    store.currentEntry = { mills: next };
    store.currentBG = 60;
    store.lastUpdated = next;
    store.now = next + 60_000;
    summary.current = { mills: next, status: GlucoseStatus.Low };

    await expect.element(page.getByTestId("glucose-tile-bloom")).toBeInTheDocument();
  });

  it("shows the reading with its unit, trend, change and age", async () => {
    render(CurrentGlucoseWidget);

    await expect.element(tile()).toHaveTextContent(/^123\s*stable\s*mg\/dL$/);
    await expect.element(page.getByText("+4", { exact: true })).toBeVisible();
    await expect.element(page.getByText(/2\s?m(in\.?)? ago/)).toBeVisible();
  });

  it("drops the trend and change of a stale reading but keeps its age", async () => {
    store.lastUpdated = now - 30 * 60_000;
    render(CurrentGlucoseWidget);

    await expect.element(page.getByText(/30\s?m(in\.?)? ago/)).toBeVisible();
    await expect.element(tile()).toHaveTextContent(/^123\s*mg\/dL$/);
    await expect.element(page.getByText("+4", { exact: true })).not.toBeInTheDocument();
  });

  it("shows the uploader battery, named for a screen reader, beside the change", async () => {
    battery.status = {
      level: 62,
      display: "62%",
      status: "ok",
      devices: { uploader: {} },
      min: { isCharging: false },
    };
    render(CurrentGlucoseWidget);

    await expect.element(page.getByText("62%")).toBeVisible();
    await expect.element(page.getByText("Uploader battery", { exact: true })).toBeInTheDocument();
  });

  it("keeps the grey wash while a new reading in the same range waits for its status", async () => {
    const first = store.currentEntry.mills;
    summary.current = { mills: first, status: GlucoseStatus.High };
    render(CurrentGlucoseWidget);
    await expect.element(page.getByTestId("artwork-wash")).toBeInTheDocument();
    const firstWash = page.getByTestId("artwork-wash").element();

    const second = first + 5 * 60_000;
    store.currentEntry = { mills: second };
    store.currentBG = 130;
    store.lastUpdated = second;
    store.now = second + 60_000;

    await expect.element(tile()).toHaveTextContent(/^123/);
    expect(page.getByTestId("artwork-wash").element()).toBe(firstWash);

    summary.current = { mills: second, status: GlucoseStatus.High };

    await expect.element(tile()).toHaveTextContent(/^130/);
    await expect.element(page.getByTestId("artwork-wash")).toBeInTheDocument();
    await expect.element(page.getByTestId("glucose-tile-bloom")).not.toBeInTheDocument();
  });

  it("drops the row under the tile when a stale reading has no battery to show", async () => {
    store.lastUpdated = now - 30 * 60_000;
    render(CurrentGlucoseWidget);
    await expect.element(page.getByText(/30\s?m(in\.?)? ago/)).toBeVisible();

    await expect.element(tile()).toBeVisible();
    const tileBottom = tile().element().getBoundingClientRect().bottom;
    const contentBottom = tile().element().parentElement!.getBoundingClientRect().bottom;
    expect(contentBottom - tileBottom).toBeLessThan(1);
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
