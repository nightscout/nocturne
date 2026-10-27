import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("#lib/api/generated/apsSnapshots.generated.remote.js", () => ({
  getAll: vi.fn(async () => ({
    data: [{ mills: Date.parse("2026-01-01T12:00:00Z"), enacted: true, currentBg: 185, eventualBg: 150, targetBg: 99 }],
  })),
}));
vi.mock("#lib/api/generated/bolusCalculations.generated.remote.js", () => ({
  getAll: vi.fn(async () => ({ data: [{ bloodGlucoseInput: 220 }] })),
}));

import { MealPeriod } from "#lib/api/index.js";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import DeliveryInspectionDialog from "./DeliveryInspectionDialog.svelte";
import GlucoseInspectionDialog from "./GlucoseInspectionDialog.svelte";
import TreatmentInspectionDialog from "./TreatmentInspectionDialog.svelte";

const timestamp = new Date("2026-01-01T12:00:00Z");

const shared = {
  open: true,
  timestamp,
  glucoseData: [],
  highThreshold: 180,
  lowThreshold: 70,
  onClose: () => {},
};

beforeEach(() => {
  glucoseUnits.current = "mg/dl";
});

describe("inspection dialogs in the chart's units", () => {
  it("shows a glucose reading and its delta in the units given, not the viewer's", async () => {
    render(GlucoseInspectionDialog, {
      ...shared,
      glucoseUnits: "mmol",
      glucoseValue: 120,
      glucoseColor: "green",
      previousGlucoseValue: 102,
      hasDeliveryContext: false,
      hasTreatmentContext: false,
    });

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("6.7", { exact: true })).toBeVisible();
    await expect.element(dialog.getByText("+1.0 mmol/L")).toBeVisible();
    await expect.element(dialog.getByText("mg/dL")).not.toBeInTheDocument();
  });

  it("shows the loop decision's glucose in the units given, not the viewer's", async () => {
    render(DeliveryInspectionDialog, {
      ...shared,
      glucoseUnits: "mmol",
      basalRate: 0.8,
      isStaleBasal: false,
      hasGlucoseContext: false,
      hasTreatmentContext: false,
    });

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("10.3 mmol/L")).toBeVisible();
    await expect.element(dialog.getByText("8.3 mmol/L")).toBeVisible();
    await expect.element(dialog.getByText("5.5 mmol/L")).toBeVisible();
    await expect.element(dialog.getByText("mg/dL")).not.toBeInTheDocument();
  });

  it("shows the treatment's glucose and the bolus calculator's input in the units given", async () => {
    render(TreatmentInspectionDialog, {
      ...shared,
      glucoseUnits: "mmol",
      bolusInsulin: 2,
      glucoseValue: 150,
      hasGlucoseContext: false,
      hasDeliveryContext: false,
    });

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("8.3 mmol/L")).toBeVisible();
    await expect.element(dialog.getByText("12.2 mmol/L")).toBeVisible();
    await expect.element(dialog.getByText("mg/dL")).not.toBeInTheDocument();
  });

  it("falls back to the viewer's units when given none", async () => {
    render(TreatmentInspectionDialog, {
      ...shared,
      bolusInsulin: 2,
      glucoseValue: 150,
      hasGlucoseContext: false,
      hasDeliveryContext: false,
    });

    await expect.element(page.getByRole("dialog").getByText("150 mg/dL")).toBeVisible();
  });
});

describe("treatment inspection dialog meal period", () => {
  it("words a meal period in sentence case when no food name was logged", async () => {
    render(TreatmentInspectionDialog, {
      ...shared,
      carbGrams: 40,
      carbMealPeriod: MealPeriod.LateNight,
      hasGlucoseContext: false,
      hasDeliveryContext: false,
    });

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("40g Late night", { exact: true })).toBeVisible();
    await expect.element(dialog.getByText("40g carbs (Late night)", { exact: true })).toBeVisible();
  });

  it("shows the food name in place of the meal period", async () => {
    render(TreatmentInspectionDialog, {
      ...shared,
      carbGrams: 40,
      carbLabel: "Porridge",
      carbMealPeriod: MealPeriod.Breakfast,
      hasGlucoseContext: false,
      hasDeliveryContext: false,
    });

    const dialog = page.getByRole("dialog");
    await expect.element(dialog.getByText("40g Porridge")).toBeVisible();
    await expect.element(dialog.getByText("Breakfast")).not.toBeInTheDocument();
  });
});
