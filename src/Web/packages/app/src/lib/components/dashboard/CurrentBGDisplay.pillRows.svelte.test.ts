import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { reactiveFields } from "$lib/test-stubs/reactive-fields.svelte";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import type {
  BasalPillData,
  COBPillData,
  IOBPillData,
  LoopPillData,
  PillInfoItem,
} from "$lib/types/status-pills";

const now = Date.UTC(2026, 5, 14, 9, 30, 0);
const store = reactiveFields({
  currentBG: 123,
  currentEntry: { mills: now, sgv: 123 },
  bgDelta: 4,
  lastUpdated: now,
  now,
  isConnected: true,
  entries: [{ mills: now, sgv: 123 }],
  direction: "Flat",
  demoMode: false,
  trackerInstances: [] as unknown[],
  trackerDefinitions: [] as unknown[],
  legacyFallbacks: [] as unknown[],
  pillsData: { cob: null, basal: null, iob: null, loop: null } as Record<string, unknown>,
  currentReservoir: null as number | null,
  isReady: false,
});

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));
vi.mock("$api/ui-settings.remote", () => ({
  getUiSettings: () => remoteQuery(() => ({ features: { trackerPills: { enabled: false } } })),
}));

import CurrentBGDisplay from "./current-bg-display-test-wrapper.svelte";

const STORAGE_KEY = "nocturne:status-pill-rows:";

const pill = { info: [] as PillInfoItem[], level: "none" as const };
const arrived = {
  cob: { ...pill, cob: 12, display: "12g", label: "COB" } satisfies COBPillData,
  basal: {
    ...pill,
    totalBasal: 0.9,
    scheduledBasal: 0.9,
    isTempBasal: false,
    isComboActive: false,
    display: "0.90U/h",
    label: "Basal",
  } satisfies BasalPillData,
  iob: { ...pill, iob: 1.25, display: "1.25U", label: "IOB" } satisfies IOBPillData,
  loop: {
    ...pill,
    status: "enacted",
    symbol: "⌁",
    display: "Enacted",
    label: "Loop",
  } satisfies LoopPillData,
};

const pillsHeight = () =>
  page.getByTestId("status-pills").element().getBoundingClientRect().height;

const arrive = async () => {
  store.isReady = true;
  store.currentReservoir = 120;
  store.pillsData = arrived;
  await expect.element(page.getByTestId("status-pills").getByText("Reservoir")).toBeVisible();
};

describe("CurrentBGDisplay pill rows", () => {
  beforeEach(() => {
    localStorage.removeItem(STORAGE_KEY);
    store.pillsData = { cob: null, basal: null, iob: null, loop: null };
    store.currentReservoir = null;
    store.isReady = false;
  });

  afterEach(() => localStorage.removeItem(STORAGE_KEY));

  // Here the empty placeholder pills fit on one row and the loaded ones wrap onto two.
  const WIDTH = 600;
  const settle = () => new Promise((resolve) => setTimeout(resolve, 300));

  it("records the rows the loaded pills filled, not the placeholders' one", async () => {
    await page.viewport(WIDTH, 900);
    render(CurrentBGDisplay);
    await settle();

    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();

    await arrive();

    await vi.waitFor(() => expect(localStorage.getItem(STORAGE_KEY)).toBe("2"));
  });

  it("holds the remembered rows through the placeholders until the pills arrive", async () => {
    await page.viewport(WIDTH, 900);
    localStorage.setItem(STORAGE_KEY, "2");
    render(CurrentBGDisplay);
    const before = pillsHeight();
    await settle();

    expect(pillsHeight()).toBe(before);
    expect(localStorage.getItem(STORAGE_KEY)).toBe("2");

    await arrive();

    expect(pillsHeight()).toBe(before);
    expect(before).toBeGreaterThan(44);
  });

  it("reserves at most four rows however many are stored", async () => {
    await page.viewport(WIDTH, 900);
    localStorage.setItem(STORAGE_KEY, "1000");
    render(CurrentBGDisplay);

    const rem = parseFloat(getComputedStyle(document.documentElement).fontSize);
    expect(pillsHeight()).toBe((4 * 2.75 + 3 * 0.25) * rem);
  });

  it("lets go of a second row once the pills fit on one", async () => {
    await page.viewport(1600, 900);
    localStorage.setItem(STORAGE_KEY, "2");
    render(CurrentBGDisplay);

    await arrive();

    await vi.waitFor(() => expect(localStorage.getItem(STORAGE_KEY)).toBe("1"));
  });
});
