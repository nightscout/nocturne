import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as appPage } from "$app/state";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { reactiveFields } from "#lib/test-stubs/reactive-fields.svelte.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { WidgetId } from "#lib/api/generated/nocturne-api-client.js";
import { dashboardTopWidgets } from "#lib/stores/appearance-store.svelte.js";
import type {
  BasalPillData,
  COBPillData,
  IOBPillData,
  LoopPillData,
  PillInfoItem,
} from "#lib/types/status-pills.js";

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

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));
vi.mock("#lib/api/ui-settings.remote.js", () => ({
  getUiSettings: () => remoteQuery(() => ({ features: { trackerPills: { enabled: false } } })),
}));

import CurrentBGDisplay from "./current-bg-display-test-wrapper.svelte";
import { PILL_ROWS_COOKIE, parsePillRows } from "#lib/components/status-pills/pill-rows.js";

const storedRows = () =>
  document.cookie
    .split("; ")
    .find((row) => row.startsWith(`${PILL_ROWS_COOKIE}=`))
    ?.slice(PILL_ROWS_COOKIE.length + 1) ?? null;
const clearStoredRows = () => {
  document.cookie = `${PILL_ROWS_COOKIE}=; Path=/; Max-Age=0`;
};
/** What the authenticated layout's server load hands the page from the cookie. */
const serverRows = (value: string | null) => {
  appPage.data.pillRows = parsePillRows(value);
};

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
    // The header carries the reading only while the Current glucose widget is off the grid,
    // and the row widths below assume the reading sits beside the pills.
    dashboardTopWidgets.hydrate([WidgetId.TirChart, WidgetId.Tdd]);
    clearStoredRows();
    serverRows(null);
    store.pillsData = { cob: null, basal: null, iob: null, loop: null };
    store.currentReservoir = null;
    store.isReady = false;
  });

  afterEach(() => {
    clearStoredRows();
    delete appPage.data.pillRows;
  });

  // Here the empty placeholder pills fit on one row and the loaded ones wrap onto two.
  const WIDTH = 600;
  const settle = () => new Promise((resolve) => setTimeout(resolve, 300));

  it("records the rows the loaded pills filled, not the placeholders' one", async () => {
    await page.viewport(WIDTH, 900);
    render(CurrentBGDisplay);
    await settle();

    expect(storedRows()).toBeNull();

    await arrive();

    await vi.waitFor(() => expect(storedRows()).toBe("2.1"));
  });

  it("holds the remembered rows through the placeholders until the pills arrive", async () => {
    await page.viewport(WIDTH, 900);
    serverRows("2.1");
    render(CurrentBGDisplay);
    const before = pillsHeight();
    await settle();

    expect(pillsHeight()).toBe(before);
    expect(storedRows()).toBeNull();

    await arrive();

    expect(pillsHeight()).toBe(before);
    expect(before).toBeGreaterThan(44);
  });

  it("reserves at most four rows however many are stored", async () => {
    await page.viewport(WIDTH, 900);
    serverRows("9.1");
    render(CurrentBGDisplay);

    const rem = parseFloat(getComputedStyle(document.documentElement).fontSize);
    expect(pillsHeight()).toBe((4 * 2.75 + 3 * 0.25) * rem);
  });

  it("lets go of a second row once the pills fit on one", async () => {
    await page.viewport(1600, 900);
    document.cookie = `${PILL_ROWS_COOKIE}=2.1; Path=/`;
    serverRows("2.1");
    render(CurrentBGDisplay);

    await arrive();

    await vi.waitFor(() => expect(storedRows()).toBe("1.1"));
  });
});
