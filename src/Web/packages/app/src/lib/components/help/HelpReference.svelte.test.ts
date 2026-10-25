import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { HelpReferenceFacts } from "$api-clients";
import HelpReference from "./HelpReference.svelte";
import { glucoseUnits } from "$lib/stores/appearance-store.svelte";

const reference = vi.hoisted(() => ({
  current: undefined as HelpReferenceFacts | undefined,
  error: undefined as unknown,
  refresh: vi.fn(),
}));
vi.mock("$api/generated/metadatas.generated.remote", () => ({
  getHelpReference: () => reference,
}));
beforeEach(() => {
  glucoseUnits.current = "mg/dl";
  reference.error = undefined;
  reference.refresh.mockClear();
  reference.current = {
    reportBands: {
      veryLow: 54,
      low: 70,
      targetBottom: 70,
      targetTop: 180,
      tightTargetBottom: 70,
      tightTargetTop: 140,
      veryHigh: 250,
    },
    maximumPlausibleGlucose: 600,
    googleHealthCallbackPath: "/settings/connectors/google-health/callback",
    googleHealthSyncIntervalMinutes: 15,
    googleHealthDefaultHistoryDays: 7,
    googleHealthMinimumHistoryDays: 1,
    googleHealthMaximumHistoryDays: 90,
    googleHealthReadScopes: [
      "https://www.googleapis.com/auth/googlehealth.sleep.readonly",
    ],
    googleHealthCapabilities: [
      { dataType: "steps", supported: true, destination: "step-counts" },
      { dataType: "blood-glucose", supported: false },
    ],
  };
});

describe("code-backed documentation reference", () => {
  it("renders changed server limits and inclusive edges without a copied default", async () => {
    reference.current!.reportBands!.targetTop = 195;
    render(HelpReference, { kind: "glucose" });
    await expect
      .element(page.getByRole("cell", { name: "70 ≤ x ≤ 195", exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByRole("cell", { name: "195 < x ≤ 250", exact: true }))
      .toBeVisible();
    await expect
      .element(
        page.getByText(
          "Example: invented readings, using the real report chart"
        )
      )
      .toBeVisible();
    await expect
      .element(page.getByText(/50%\s+is\s+already\s+included/))
      .toBeVisible();
  });
  it("uses the live capability and scope catalogue, with non-interactive shared examples", async () => {
    render(HelpReference, { kind: "google-health" });
    await expect
      .element(page.getByRole("cell", { name: "Steps", exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByRole("cell", { name: "Blood glucose", exact: true }))
      .not.toBeInTheDocument();
    await expect
      .element(
        page.getByText(
          "https://www.googleapis.com/auth/googlehealth.sleep.readonly",
          { exact: true }
        )
      )
      .toBeVisible();
    expect(page.getByRole("button").all()).toHaveLength(0);
  });
  it("offers a retry when reference loading fails, without showing invented limits", async () => {
    reference.current = undefined;
    reference.error = new Error("offline");
    render(HelpReference, { kind: "glucose" });
    await expect
      .element(page.getByRole("status"))
      .toHaveTextContent("could not be loaded");
    await page.getByRole("button", { name: "Retry reference values" }).click();
    expect(reference.refresh).toHaveBeenCalledOnce();
    await expect.element(page.getByRole("table")).not.toBeInTheDocument();
  });
});
