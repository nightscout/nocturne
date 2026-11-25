import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { GlucoseStatus } from "@nocturne/api-client";
import { glucoseChartLookback } from "#lib/stores/appearance-store.svelte.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const { now, store, settings, summary } = vi.hoisted(() => {
  const now = Date.UTC(2026, 5, 14, 9, 30, 0);
  return {
    now,
    store: {
      currentBG: 160,
      currentEntry: { mills: now, sgv: 160 },
      bgDelta: -4,
      lastUpdated: now - 2 * 60 * 1000,
      now,
      isConnected: true,
      connectionUnavailable: false,
      entries: [{ mills: now, sgv: 160 }],
      direction: "FortyFiveDown",
      demoMode: false,
      currentReservoir: 45.5 as number | null,
      currentLinkBatteryVoltage: null,
      pillsData: {
        iob: { display: "3.27U", label: "IOB", info: [], level: "none", iob: 3.27 },
        cob: { display: "11g", label: "COB", info: [], level: "none", cob: 11 },
        basal: {
          display: "1.200U",
          label: "Basal",
          info: [],
          level: "none",
          totalBasal: 1.2,
          scheduledBasal: 1.2,
          isTempBasal: false,
          isComboActive: false,
        },
        loop: {
          display: "",
          label: "Loop",
          info: [],
          level: "none",
          status: "looping",
          symbol: "↻",
          lastLoopTime: now - 2 * 60 * 1000,
          loopName: "Loop",
          eventualBG: 101,
        },
      },
      trackerInstances: [] as unknown[],
      trackerDefinitions: [] as unknown[],
      legacyFallbacks: [] as unknown[],
      isReady: true,
      noteTreatmentWrite: () => {},
      refreshTrackers: () => {},
    },
    settings: { features: { trackerPills: { enabled: false } } },
    summary: { current: undefined as { mills: number; status: string } | undefined },
  };
});

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
  tryGetRealtimeStore: () => store,
}));

vi.mock("#lib/api/ui-settings.remote.js", () => ({
  getUiSettings: () => remoteQuery(() => settings),
}));

vi.mock("#lib/api/generated/summaries.generated.remote.js", () => ({
  getSummary: () => remoteQuery(() => ({ current: summary.current })),
}));

vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"));

import CurrentBGDisplay from "./current-bg-display-test-wrapper.svelte";

const header = () => page.getByTestId("nightscout-status");
const pills = () =>
  Array.from(document.querySelectorAll<HTMLElement>("[data-testid=nightscout-status] [data-pill]")).map(
    (pill) => ({
      label: pill.querySelector("[data-pill-label]")?.textContent?.trim(),
      value: pill.querySelector("[data-pill-value]")?.textContent?.trim(),
    })
  );

describe("CurrentBGDisplay layout=nightscout", () => {
  beforeEach(() => {
    glucoseChartLookback.current = 4;
    summary.current = { mills: now, status: GlucoseStatus.InRange };
  });

  afterEach(() => {
    store.lastUpdated = now - 2 * 60 * 1000;
    store.currentReservoir = 45.5;
  });

  it("lays out the clock, reading, trend arrow and Hours row", async () => {
    render(CurrentBGDisplay, { layout: "nightscout" });

    await expect.element(header()).toBeInTheDocument();
    const root = document.querySelector("[data-testid=nightscout-status]")!;
    expect(root.querySelector(".ns-current-bg")?.textContent).toContain("160");
    expect(root.querySelector(".ns-direction")?.textContent).toContain("↘");
    expect(root.querySelector(".ns-clock")?.textContent?.trim()).not.toBe("");
    expect(page.getByRole("radio").elements().map((e) => e.textContent?.trim())).toEqual([
      "2", "3", "4", "6", "12", "24", "48",
    ]);
  });

  it("colours the reading by its status", async () => {
    summary.current = { mills: now, status: GlucoseStatus.High };
    render(CurrentBGDisplay, { layout: "nightscout" });

    await expect.element(header()).toBeInTheDocument();
    await vi.waitFor(() =>
      expect(document.querySelector(".ns-bg-button")?.classList).toContain("text-glucose-high")
    );
  });

  it("shows the Nightscout pills, the delta and minutes ago value first", async () => {
    render(CurrentBGDisplay, { layout: "nightscout" });

    await expect.element(header()).toBeInTheDocument();
    await vi.waitFor(() => {
      expect(pills()).toEqual(
        expect.arrayContaining([
          { label: "mg/dL", value: "-4" },
          { label: "IOB", value: "3.27U" },
          { label: "COB", value: "11g" },
          { label: "Basal", value: "1.200U" },
          { label: "min ago", value: "2" },
          { label: "Pump", value: "45.5U" },
        ])
      );
    });
    const flipped = document.querySelectorAll("[data-pill-flip] [data-pill]");
    expect(flipped).toHaveLength(2);
  });

  it("words the loop pill as Nightscout does", async () => {
    render(CurrentBGDisplay, { layout: "nightscout" });

    await vi.waitFor(() => {
      const loop = pills().find((p) => p.label?.startsWith("Loop"));
      expect(loop?.label).toBe("Loop ↻");
      expect(loop?.value).toContain("↝");
    });
  });

  it("leaves out the pump pill when the pump reports no reservoir", async () => {
    store.currentReservoir = null;
    render(CurrentBGDisplay, { layout: "nightscout" });

    await expect.element(header()).toBeInTheDocument();
    expect(pills().map((p) => p.label)).not.toContain("Pump");
  });

  it("strikes through a stale reading and marks its age urgent", async () => {
    store.lastUpdated = now - 60 * 60 * 1000;
    render(CurrentBGDisplay, { layout: "nightscout" });

    await expect.element(header()).toBeInTheDocument();
    expect(document.querySelector(".ns-bg-button")?.classList).toContain("ns-stale");
    const age = Array.from(document.querySelectorAll("[data-pill-flip] [data-pill-value]")).find(
      (v) => v.textContent?.includes("60")
    );
    // The urgent level also carries an icon and its screen-reader word.
    expect(age?.textContent).toContain("Urgent:");
    expect(age?.classList).toContain("text-severity-urgent");
  });

  it("sets the chart window from the Hours row and keeps it when clicked again", async () => {
    render(CurrentBGDisplay, { layout: "nightscout" });

    const twelve = page.getByRole("radio", { name: "12", exact: true });
    await twelve.click();
    expect(glucoseChartLookback.current).toBe(12);
    await expect.element(twelve).toHaveAttribute("data-state", "on");

    await twelve.click();
    expect(glucoseChartLookback.current).toBe(12);
    await expect.element(twelve).toHaveAttribute("data-state", "on");
  });
});
