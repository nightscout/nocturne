import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import {
  LegacyConsumableKind,
  NotificationUrgency,
  TrackerCategory,
  type LegacyFallbackDto,
} from "#lib/api/generated/nocturne-api-client.js";
import { hiddenLegacyFallbacks } from "#lib/stores/appearance-store.svelte.js";
import LegacyAgePill from "./LegacyAgePill.svelte";
import LegacyAgePillRail from "./LegacyAgePill.test-host.svelte";

const HOUR = 3600_000;
const now = Date.UTC(2026, 9, 25, 12, 0, 0);
const at = (hoursAgo: number) => new Date(now - hoursAgo * HOUR).toISOString();

// The pref writes through to localStorage, which every later test file's store reads at load.
const HIDDEN_STORAGE_KEY = "nocturne-hidden-legacy-fallbacks";

const SITE: LegacyFallbackDto = {
  kind: LegacyConsumableKind.Site,
  latestEventAt: at(50),
  // A stale snapshot from the read; the pill ages from latestEventAt on the store's clock.
  ageHours: 40,
  schedule: [
    { urgency: NotificationUrgency.Info, firesAt: at(6) },
    { urgency: NotificationUrgency.Warn, firesAt: at(2) },
    { urgency: NotificationUrgency.Urgent, firesAt: new Date(now + 22 * HOUR).toISOString() },
  ],
  spec: { name: "Omnipod DASH pod", category: TrackerCategory.Cannula, lifespanHours: 72 },
};

const SENSOR: LegacyFallbackDto = {
  kind: LegacyConsumableKind.Sensor,
  latestEventAt: at(30),
  ageHours: 30,
  schedule: [{ urgency: NotificationUrgency.Hazard, firesAt: at(1) }],
  spec: { name: "CGM Sensor", category: TrackerCategory.Sensor, lifespanHours: 240 },
};

const pill = (name = /Infusion site/) => page.getByRole("button", { name });
const createButton = () => page.getByRole("button", { name: "Create a tracker" });
const hideButton = () => page.getByRole("button", { name: "Hide" });

describe("LegacyAgePill", () => {
  beforeEach(() => {
    hiddenLegacyFallbacks.presentFrom({});
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
  });

  afterEach(() => {
    hiddenLegacyFallbacks.presentFrom({});
    localStorage.removeItem(HIDDEN_STORAGE_KEY);
  });

  it("names the kind and shows its age in the tone of the step reached", async () => {
    render(LegacyAgePill, { fallback: SITE, now });

    await expect.element(pill()).toHaveTextContent("2d 2h");
    await expect.element(pill().getByText("Warning:")).toBeInTheDocument();
  });

  it("ages on the clock it is given, as its tone does", async () => {
    const screen = render(LegacyAgePill, { fallback: SITE, now });

    await screen.rerender({ fallback: SITE, now: now + 3 * HOUR });

    await expect.element(pill()).toHaveTextContent("2d 5h");
  });

  it("announces a hazard as a hazard", async () => {
    render(LegacyAgePill, { fallback: SENSOR, now });

    await expect.element(pill(/Sensor/).getByText("Hazard:")).toBeInTheDocument();
  });

  it("shows the age and the last change in its popover", async () => {
    render(LegacyAgePill, { fallback: SITE, now });

    await pill().click();

    const popover = page.getByTestId("legacy-age-pill-popover");
    await expect.element(popover.getByText("Last change", { exact: true })).toBeVisible();
    await expect.element(popover.getByText("3 days")).toBeVisible();
  });

  it("offers a member with alerts.readwrite to create a tracker for this kind", async () => {
    const onCreateTracker = vi.fn();
    render(LegacyAgePill, { fallback: SITE, now, onCreateTracker });

    await pill().click();
    await createButton().click();

    expect(onCreateTracker).toHaveBeenCalledWith(LegacyConsumableKind.Site);
  });

  it("hides the kind for this user", async () => {
    hiddenLegacyFallbacks.hydrate([LegacyConsumableKind.Sensor]);
    render(LegacyAgePill, { fallback: SITE, now });

    await pill().click();
    await hideButton().click();

    expect(hiddenLegacyFallbacks.current).toEqual([
      LegacyConsumableKind.Sensor,
      LegacyConsumableKind.Site,
    ]);
  });

  it("lets a reader without alerts.readwrite hide the age but not create a tracker", async () => {
    pageState.data = { effectivePermissions: ["alerts.read", "devices.read"] };
    render(LegacyAgePill, { fallback: SITE, now });

    await pill().click();

    await expect.element(hideButton()).toBeVisible();
    await expect.element(createButton()).not.toBeInTheDocument();
  });

  it("moves focus to the next pill once one is hidden", async () => {
    render(LegacyAgePillRail, { fallbacks: [SITE, SENSOR], now });

    await pill().click();
    await hideButton().click();

    await expect.element(pill()).not.toBeInTheDocument();
    await expect.element(pill(/Sensor/)).toHaveFocus();
  });

  it("moves focus to the rail once the last pill is hidden", async () => {
    render(LegacyAgePillRail, { fallbacks: [SITE], now });

    await pill().click();
    await hideButton().click();

    await expect.element(page.getByTestId("rail")).toHaveFocus();
  });

  it("gives the rail focus only for the hide, not for a later click on it", async () => {
    render(LegacyAgePillRail, { fallbacks: [SITE], now });

    await pill().click();
    await hideButton().click();
    await expect.element(page.getByTestId("rail")).toHaveFocus();
    (document.activeElement as HTMLElement).blur();

    expect(page.getByTestId("rail").element().hasAttribute("tabindex")).toBe(false);
  });
});
