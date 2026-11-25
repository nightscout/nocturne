import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { setMode } from "mode-watcher";
import { glucoseChartLookback } from "#lib/stores/appearance-store.svelte.js";

const { goto, NOW } = vi.hoisted(() => ({
  NOW: Date.UTC(2026, 9, 25, 12, 0, 0),
  goto: vi.fn(() => Promise.resolve()),
}));

// Reactive, so a live value changing while the palette is open reaches it.
const store = $state({
  pillsData: {} as { iob?: { display: string }; cob?: { display: string } },
  nowMinute: NOW,
  legacyFallbacks: [] as unknown[],
  legacyCoverage: [] as unknown[],
  trackerInstances: [] as unknown[],
});

vi.mock("$app/navigation", () => ({ goto }));

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
}));

vi.mock("./command-palette-items.svelte.js", async (importOriginal) => {
  const items = await importOriginal<typeof import("./command-palette-items.svelte.js")>();
  return { ...items, paletteItemsFor: vi.fn(items.paletteItemsFor) };
});

// The vitals strip reads live glucose; the palette under test only owns its items.
vi.mock("./CommandPaletteVitals.svelte", () => ({ default: () => {} }));

import CommandPalette from "./CommandPalette.svelte";
import { paletteItemsFor } from "./command-palette-items.svelte.js";
import { pinnedItemIds, recentItemIds } from "./command-palette-store.svelte";

const item = (name: string) => page.getByRole("option", { name, exact: true });

// A report option's name carries its description after the title.
const report = (title: string) =>
  page.getByRole("option", { name: new RegExp(`^${title}`) });

describe("CommandPalette", () => {
  beforeEach(() => {
    goto.mockClear();
    pinnedItemIds.current = [];
    recentItemIds.current = [];
    pageState.data = { effectivePermissions: [], isPlatformAdmin: false };
    store.pillsData = {};
    store.legacyFallbacks = [];
    store.legacyCoverage = [];
    store.trackerInstances = [];
  });

  afterEach(() => {
    setMode("system");
    glucoseChartLookback.current = 12;
  });

  // The palette's ages run on the store's clock from the timestamps the server sent.
  const hoursAgo = (hours: number) => new Date(NOW - hours * 3600_000).toISOString();

  it("shows an uncovered kind's age from its legacy fallback", async () => {
    store.legacyFallbacks = [{ kind: "Site", latestEventAt: hoursAgo(50), ageHours: 1 }];

    render(CommandPalette, { open: true });

    await expect.element(page.getByRole("option", { name: /^CAGE: 2d 2h/ })).toBeVisible();
  });

  it("shows a covered kind's age from the newest run of a covering tracker", async () => {
    store.legacyCoverage = [{ kind: "Sensor", definitionIds: ["dexcom", "libre"] }];
    store.trackerInstances = [
      { id: "a", definitionId: "dexcom", startedAt: hoursAgo(30) },
      { id: "b", definitionId: "libre", startedAt: hoursAgo(5) },
      { id: "c", definitionId: "appointment", startedAt: hoursAgo(1) },
    ];

    render(CommandPalette, { open: true });

    await expect.element(page.getByRole("option", { name: /^SAGE: 5h/ })).toBeVisible();
  });

  it("shows no age for a covered kind whose trackers are not running", async () => {
    store.legacyCoverage = [{ kind: "Sensor", definitionIds: ["dexcom"] }];
    store.trackerInstances = [{ id: "c", definitionId: "appointment", startedAt: hoursAgo(1) }];

    render(CommandPalette, { open: true });

    await expect.element(page.getByRole("option", { name: /^SAGE(?!:)/ })).toBeVisible();
  });

  it("renders no button inside an option or link", async () => {
    render(CommandPalette, { open: true, tenantless: true });

    // The palette selects its first item a tick after mounting; unmounting before then
    // leaves bits-ui reading a detached ref.
    await expect
      .element(page.getByRole("option").first())
      .toHaveAttribute("aria-selected", "true");

    expect(
      page.getByRole("option").getByRole("button").elements()
    ).toHaveLength(0);
    expect(page.getByRole("link").getByRole("button").elements()).toHaveLength(
      0
    );
  });

  it("pins an item without following its link", async () => {
    // Captured before the pin's own handler runs, so stopping propagation cannot hide it.
    let clickedInsideLink = false;
    const recordClick = (event: MouseEvent) => {
      const target = event.target;
      if (target instanceof Element && target.closest("a"))
        clickedInsideLink = true;
      event.preventDefault();
    };
    window.addEventListener("click", recordClick, true);
    render(CommandPalette, { open: true, tenantless: true });

    await page.getByRole("button", { name: "Pin Appearance" }).click();
    window.removeEventListener("click", recordClick, true);

    await expect
      .element(page.getByRole("button", { name: "Unpin Appearance" }))
      .toBeInTheDocument();
    expect(pinnedItemIds.current).toContain("settings-appearance");
    expect(goto).not.toHaveBeenCalled();
    expect(clickedInsideLink).toBe(false);
  });

  it("offers Audit to a viewer holding audit.read", async () => {
    pageState.data = { effectivePermissions: ["audit.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Audit")).toBeVisible();
  });

  it("offers Audit to a viewer holding audit.manage", async () => {
    pageState.data = { effectivePermissions: ["audit.manage"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Audit")).toBeVisible();
  });

  it("withholds Audit from a viewer holding neither audit scope", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Audit")).not.toBeInTheDocument();
  });

  it("offers Alerts to a viewer holding alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Alerts")).toBeVisible();
  });

  it("withholds Alerts from a viewer holding neither alerts scope", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Alerts")).not.toBeInTheDocument();
  });

  it("offers Grants to a viewer holding sharing.manage", async () => {
    pageState.data = { effectivePermissions: ["sharing.manage"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Grants")).toBeVisible();
  });

  it("withholds Grants from a viewer without sharing.manage", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Grants")).not.toBeInTheDocument();
  });

  it("offers the platform admin pages to a platform administrator", async () => {
    pageState.data = { effectivePermissions: [], isPlatformAdmin: true };
    render(CommandPalette, { open: true });

    await expect.element(item("All tenants")).toBeVisible();
    await expect.element(item("Access requests")).toBeVisible();
  });

  it("withholds the platform admin pages from everyone else", async () => {
    pageState.data = { effectivePermissions: ["*"], isPlatformAdmin: false };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("All tenants")).not.toBeInTheDocument();
    await expect.element(item("Access requests")).not.toBeInTheDocument();
  });

  it("lists a report under the registry's title to a viewer holding its scopes", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(report("Day-by-day view")).toBeVisible();
  });

  it("withholds a report from a viewer without its scope", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(report("Day-by-day view")).toBeVisible();
    await expect.element(report("Battery")).not.toBeInTheDocument();
  });

  it("gives a quick setting one label for its text, its search value and its pin, and follows a toggle", async () => {
    setMode("dark");
    render(CommandPalette, { open: true });

    const option = () => page.getByRole("option", { name: /^Dark mode: / });
    await expect.element(option()).toHaveTextContent(/^Dark mode: On/);
    await expect.element(option()).toHaveAttribute("data-value", "Dark mode: On");
    await expect
      .element(page.getByRole("button", { name: "Pin Dark mode: On" }))
      .toBeInTheDocument();

    await option().click();

    await expect.element(option()).toHaveTextContent(/^Dark mode: Off/);
    await expect.element(option()).toHaveAttribute("data-value", "Dark mode: Off");
    await expect
      .element(page.getByRole("button", { name: "Pin Dark mode: Off" }))
      .toBeInTheDocument();
  });

  it("builds no items while closed, whatever the live values do", async () => {
    render(CommandPalette, { open: false });
    await new Promise((settle) => setTimeout(settle, 50));
    vi.mocked(paletteItemsFor).mockClear();

    store.nowMinute += 60_000;
    store.pillsData = { iob: { display: "1.3 U" } };
    await new Promise((settle) => setTimeout(settle, 50));

    expect(paletteItemsFor).not.toHaveBeenCalled();
  });

  it("gives a stat one label for its text, its search value and its pin, and finds it by its value", async () => {
    store.pillsData = { iob: { display: "1.2 U" } };
    render(CommandPalette, { open: true });

    const option = () => page.getByRole("option", { name: /^IOB/ });
    await expect.element(option()).toHaveTextContent(/^IOB: 1\.2 U/);
    await expect.element(option()).toHaveAttribute("data-value", "IOB: 1.2 U");
    await expect
      .element(page.getByRole("button", { name: "Pin IOB: 1.2 U" }))
      .toBeInTheDocument();

    await page.getByPlaceholder("Search commands...").fill("1.2");

    await expect.element(option()).toBeVisible();
    await expect.element(page.getByRole("option", { name: /^COB/ })).not.toBeInTheDocument();
  });

  it("names a stat with no value by its name alone", async () => {
    render(CommandPalette, { open: true });

    await expect
      .element(page.getByRole("option", { name: /^IOB(?!:)/ }))
      .toHaveAttribute("data-value", "IOB");
    await expect
      .element(page.getByRole("button", { name: "Pin IOB" }))
      .toBeInTheDocument();
  });

  it("keeps the keyboard highlight where it is when a stat's value changes", async () => {
    store.pillsData = { iob: { display: "1.2 U" } };
    pinnedItemIds.current = ["stat-iob"];
    recentItemIds.current = ["qs-chart-lookback"];
    render(CommandPalette, { open: true });

    const lookback = page.getByRole("option", { name: /^Chart lookback: / });
    await expect
      .element(page.getByRole("option", { name: /^IOB/ }))
      .toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{ArrowDown}");
    await expect.element(lookback).toHaveAttribute("aria-selected", "true");

    store.pillsData = { iob: { display: "1.3 U" } };

    await expect
      .element(page.getByRole("option", { name: /^IOB/ }))
      .toHaveTextContent(/^IOB: 1\.3 U/);
    await new Promise((settle) => setTimeout(settle, 150));
    await expect.element(lookback).toHaveAttribute("aria-selected", "true");
  });

  it("keeps the highlight on a stat whose own value changes", async () => {
    store.pillsData = { iob: { display: "1.2 U" }, cob: { display: "5 g" } };
    pinnedItemIds.current = ["stat-cob", "stat-iob"];
    render(CommandPalette, { open: true });

    const iob = page.getByRole("option", { name: /^IOB/ });
    await expect
      .element(page.getByRole("option", { name: /^COB/ }))
      .toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{ArrowDown}");
    await expect.element(iob).toHaveAttribute("aria-selected", "true");

    store.pillsData = { iob: { display: "1.3 U" }, cob: { display: "5 g" } };

    await expect.element(iob).toHaveTextContent(/^IOB: 1\.3 U/);
    await new Promise((settle) => setTimeout(settle, 150));
    await expect.element(iob).toHaveAttribute("aria-selected", "true");
  });

  it("moves the highlight to a rendered item when a stat's new value no longer matches the search", async () => {
    store.pillsData = { iob: { display: "1.2 U" }, cob: { display: "25 g" } };
    render(CommandPalette, { open: true });

    const iob = page.getByRole("option", { name: /^IOB/ });
    await page.getByPlaceholder("Search commands...").fill("2");
    await expect.element(iob).toBeVisible();
    await userEvent.hover(iob);
    await expect.element(iob).toHaveAttribute("aria-selected", "true");

    store.pillsData = { iob: { display: "1.3 U" }, cob: { display: "25 g" } };

    await expect.element(iob).not.toBeInTheDocument();
    await new Promise((settle) => setTimeout(settle, 150));
    await expect.element(page.getByRole("option", { name: /^COB/ })).toBeVisible();
    await expect
      .element(page.getByRole("option", { selected: true }))
      .toBeInTheDocument();
  });

  it("keeps the highlight on a quick setting after Enter, so Enter cycles it again", async () => {
    pinnedItemIds.current = ["stat-iob"];
    recentItemIds.current = ["qs-chart-lookback"];
    glucoseChartLookback.current = 12;
    render(CommandPalette, { open: true });

    const lookback = page.getByRole("option", { name: /^Chart lookback: / });
    await expect
      .element(page.getByRole("option", { name: /^IOB/ }))
      .toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{ArrowDown}");
    await expect.element(lookback).toHaveAttribute("aria-selected", "true");

    await userEvent.keyboard("{Enter}");
    await expect.element(lookback).toHaveTextContent(/^Chart lookback: 24h/);
    await expect.element(lookback).toHaveAttribute("aria-selected", "true");

    await userEvent.keyboard("{Enter}");
    await expect.element(lookback).toHaveTextContent(/^Chart lookback: 48h/);
    await expect.element(lookback).toHaveAttribute("aria-selected", "true");
    expect(glucoseChartLookback.current).toBe(48);
  });

  it("hands the highlight back to search when text arrives without a key press", async () => {
    pinnedItemIds.current = ["stat-iob"];
    recentItemIds.current = ["qs-chart-lookback"];
    glucoseChartLookback.current = 12;
    render(CommandPalette, { open: true });

    const lookback = page.getByRole("option", { name: /^Chart lookback: / });
    await expect
      .element(page.getByRole("option", { name: /^IOB/ }))
      .toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{ArrowDown}");
    await userEvent.keyboard("{Enter}");
    await expect.element(lookback).toHaveTextContent(/^Chart lookback: 24h/);

    await page.getByPlaceholder("Search commands...").fill("dark");

    await expect
      .element(page.getByRole("option", { selected: true }))
      .toBeInTheDocument();
  });
});
