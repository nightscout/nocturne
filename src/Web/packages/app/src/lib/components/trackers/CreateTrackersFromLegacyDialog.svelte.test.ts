import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  LegacyConsumableKind,
  TrackerCategory,
  type LegacyConversionDto,
  type LegacyFallbackDto,
} from "$lib/api/generated/nocturne-api-client";

const { createFromLegacy, coach } = vi.hoisted(() => ({
  createFromLegacy: vi.fn<(request: { kinds: string[] }) => Promise<LegacyConversionDto>>(),
  coach: { startSequence: vi.fn() },
}));

vi.mock("$api/generated/trackerLegacyFallbacks.generated.remote", () => ({
  createFromLegacy,
}));

vi.mock("@nocturne/coach", () => ({ getCoachMarkContext: () => coach }));

import CreateTrackersFromLegacyDialog from "./CreateTrackersFromLegacyDialog.svelte";

const fallback = (
  kind: LegacyConsumableKind,
  name: string,
  category: TrackerCategory,
  lifespanHours: number
): LegacyFallbackDto => ({
  kind,
  latestEventAt: "2026-10-23T08:00:00Z",
  ageHours: 50,
  spec: { name, category, lifespanHours },
});

const FALLBACKS = [
  fallback(LegacyConsumableKind.Sensor, "CGM Sensor", TrackerCategory.Sensor, 240),
  fallback(LegacyConsumableKind.Site, "Infusion Site", TrackerCategory.Cannula, 72),
  fallback(LegacyConsumableKind.PumpBattery, "Pump Battery", TrackerCategory.Battery, 504),
];

const checkbox = (name: string) => page.getByRole("checkbox", { name });
const submit = () => page.getByRole("button", { name: "Create trackers" });

function open(initialKind?: LegacyConsumableKind, onCreated = vi.fn()) {
  render(CreateTrackersFromLegacyDialog, {
    open: true,
    fallbacks: FALLBACKS,
    initialKind,
    onCreated,
  });
  return onCreated;
}

describe("CreateTrackersFromLegacyDialog", () => {
  beforeEach(() => {
    createFromLegacy.mockReset();
    coach.startSequence.mockReset();
  });

  it("lists every uncovered kind with the clicked one selected", async () => {
    open(LegacyConsumableKind.Site);

    await expect.element(checkbox("Infusion site")).toBeChecked();
    await expect.element(checkbox("Sensor")).not.toBeChecked();
    await expect.element(checkbox("Pump battery")).not.toBeChecked();
    await expect.element(page.getByText(/Lifespan: 10 days/)).toBeVisible();
  });

  it("converts the selected kinds and lists the trackers it created", async () => {
    createFromLegacy.mockResolvedValue({
      definitions: [{ id: "d1", name: "Infusion Site" }, { id: "d2", name: "CGM Sensor" }],
      instances: [],
      failedKinds: [],
    });
    const onCreated = open(LegacyConsumableKind.Site);

    await checkbox("Sensor").click();
    await submit().click();

    expect(createFromLegacy).toHaveBeenCalledWith({
      kinds: [LegacyConsumableKind.Site, LegacyConsumableKind.Sensor],
    });
    const outcome = page.getByTestId("legacy-conversion-result");
    await expect.element(outcome.getByText("Created")).toBeVisible();
    await expect.element(outcome.getByText("CGM Sensor")).toBeVisible();
    expect(onCreated).toHaveBeenCalledOnce();
  });

  it("names the kinds whose conversion failed", async () => {
    createFromLegacy.mockResolvedValue({
      definitions: [{ id: "d1", name: "Infusion Site" }],
      instances: [],
      failedKinds: [LegacyConsumableKind.PumpBattery],
    });
    open(LegacyConsumableKind.Site);

    await checkbox("Pump battery").click();
    await submit().click();

    const outcome = page.getByTestId("legacy-conversion-result");
    await expect.element(outcome.getByText("Not created")).toBeVisible();
    await expect.element(outcome.getByText("Pump battery")).toBeVisible();
  });

  it("reports a refused request and keeps the selection", async () => {
    createFromLegacy.mockRejectedValue({
      status: 403,
      body: { message: "Creating trackers needs alerts.readwrite." },
    });
    const onCreated = open(LegacyConsumableKind.Site);

    await submit().click();

    await expect.element(page.getByRole("alert")).toBeVisible();
    await expect.element(checkbox("Infusion site")).toBeChecked();
    expect(onCreated).not.toHaveBeenCalled();
  });

  it("does not submit with nothing selected", async () => {
    open();

    await expect.element(submit()).toBeDisabled();
  });

  it("starts the tracker introduction when it closes after creating a tracker", async () => {
    createFromLegacy.mockResolvedValue({
      definitions: [{ id: "d1", name: "Infusion Site" }],
      instances: [],
      failedKinds: [],
    });
    open(LegacyConsumableKind.Site);

    await submit().click();
    await page.getByRole("button", { name: "Done" }).click();

    expect(coach.startSequence).toHaveBeenCalledExactlyOnceWith("tracker-intro");
  });

  it("starts no introduction when nothing was created", async () => {
    createFromLegacy.mockResolvedValue({
      definitions: [],
      instances: [],
      failedKinds: [LegacyConsumableKind.Site],
    });
    open(LegacyConsumableKind.Site);

    await submit().click();
    await page.getByRole("button", { name: "Done" }).click();

    expect(coach.startSequence).not.toHaveBeenCalled();
  });
});
