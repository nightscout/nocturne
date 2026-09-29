import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "$lib/test-stubs/remote-resource";
import type { TherapyReview } from "$api";

// $state, so a refresh after a conflict re-renders with the profile that arrived.
let reviewState = $state<unknown>(undefined);

const therapy = vi.hoisted(() => ({
  confirm: vi.fn(),
  enter: vi.fn(),
}));
vi.mock("$api/generated/setupTherapies.generated.remote", () => ({
  getTherapyReview: () => remoteQuery(() => reviewState),
  confirmTherapySettings: remoteCommand(therapy.confirm),
  enterTherapySettings: remoteCommand(therapy.enter),
}));

const hubRefresh = vi.hoisted(() => vi.fn());
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () => ({ refresh: hubRefresh }),
}));

const tenant = vi.hoisted(() => ({
  relationship: {} as { relationship?: string; patientName?: string },
}));
vi.mock("$api/generated/tenantSettings.generated.remote", () => ({
  getPatientRelationship: () => remoteQuery(() => tenant.relationship),
}));

vi.mock("$api/generated/profiles.generated.remote", () => ({
  createTargetRangeSchedule: vi.fn(),
}));

import { TherapyGlucoseField, TherapySource } from "$api";
import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
import TherapyPage from "./+page.svelte";

const RULES = [
  { field: TherapyGlucoseField.Sensitivity, glucoseUnits: "mg/dl", below: 8 },
  { field: TherapyGlucoseField.Sensitivity, glucoseUnits: "mmol", above: 25 },
  { field: TherapyGlucoseField.Target, glucoseUnits: "mg/dl", below: 30 },
  { field: TherapyGlucoseField.Target, glucoseUnits: "mmol", above: 25 },
];

function reviewOf(source: TherapySource, extra: Partial<TherapyReview> = {}): TherapyReview {
  if (source === TherapySource.None) return { source, confirmed: false, wrongUnitRules: RULES };
  return {
    source,
    confirmed: false,
    settings: { profileName: "Default" } as TherapyReview["settings"],
    basal: { entries: [{ time: "00:00", value: 0.8 }] } as TherapyReview["basal"],
    carbRatio: { entries: [{ time: "00:00", value: 10 }] } as TherapyReview["carbRatio"],
    sensitivity: { entries: [{ time: "00:00", value: 54 }] } as TherapyReview["sensitivity"],
    targetRange: { entries: [{ time: "00:00", low: 99, high: 126 }] } as TherapyReview["targetRange"],
    wrongUnitRules: RULES,
    ...extra,
  };
}

const section = (title: string) =>
  page.getByTestId("therapy-entry").getByRole("heading", { name: title }).element()
    .closest(".space-y-4") as HTMLElement;

beforeEach(() => {
  tenant.relationship = { relationship: "Self" };
  glucoseUnits.current = "mmol";
  therapy.confirm.mockReset().mockResolvedValue({});
  therapy.enter.mockReset().mockResolvedValue({});
  hubRefresh.mockReset().mockResolvedValue(undefined);
});

describe("therapy settings, nothing yet", () => {
  beforeEach(() => {
    reviewState = reviewOf(TherapySource.None);
  });

  it("says what the values feed and that Nocturne never suggests doses", async () => {
    render(TherapyPage);

    await expect
      .element(page.getByText("Enter the values your care team set. Nocturne never suggests or adjusts doses."))
      .toBeVisible();
    await expect
      .element(page.getByTestId("therapy-entry"))
      .toHaveTextContent(
        "to estimate insulin on board, carbs on board and where your glucose may be heading. Apps connected to Nocturne may show those estimates."
      );
    await expect.element(page.getByRole("checkbox")).not.toBeInTheDocument();
  });

  it("speaks of a named patient's care team", async () => {
    tenant.relationship = { relationship: "Caregiver", patientName: "Sam" };
    render(TherapyPage);

    await expect
      .element(page.getByText("Enter the values Sam's care team set. Nocturne never suggests or adjusts doses."))
      .toBeVisible();
  });

  it("pre-fills nothing, even in a block added later", async () => {
    render(TherapyPage);
    await expect.element(page.getByTestId("therapy-entry")).toBeVisible();

    await page.getByRole("button", { name: "Add Time Block" }).first().click();

    const values = Array.from(
      page.getByTestId("therapy-entry").element().querySelectorAll<HTMLInputElement>("input[type=number]")
    ).map((input) => input.value);
    expect(values).toHaveLength(6);
    expect(values.every((v) => v === "")).toBe(true);
  });

  it("puts the chosen unit beside every sensitivity and target field", async () => {
    render(TherapyPage);
    await expect.element(page.getByTestId("therapy-entry")).toBeVisible();

    const units = (title: string) =>
      Array.from(section(title).querySelectorAll("[data-testid=field-unit]")).map((u) => u.textContent);
    expect(units("Insulin sensitivity")).toEqual(["mmol/L/U"]);
    expect(units("Target range")).toEqual(["mmol/L", "mmol/L"]);
  });

  it("flags a value that reads as the other unit, in mmol/L", async () => {
    render(TherapyPage);
    const isf = page.getByRole("spinbutton", { name: "Insulin sensitivity from 00:00" });

    await userEvent.fill(isf, "50");
    await userEvent.tab();
    await expect.element(page.getByText(/This looks like a value in mg\/dL, but these fields are in mmol\/L\./)).toBeVisible();

    await userEvent.fill(isf, "3");
    await userEvent.tab();
    await expect.element(page.getByText(/This looks like a value in/)).not.toBeInTheDocument();
  });

  it("flags a value that reads as the other unit, in mg/dL", async () => {
    glucoseUnits.current = "mg/dl";
    render(TherapyPage);

    await userEvent.fill(page.getByRole("spinbutton", { name: "Low from 00:00" }), "5.5");
    await userEvent.tab();
    await expect.element(page.getByText(/This looks like a value in mmol\/L, but these fields are in mg\/dL\./)).toBeVisible();

    await userEvent.fill(page.getByRole("spinbutton", { name: "Insulin sensitivity from 00:00" }), "2");
    await userEvent.tab();
    await expect.element(page.getByText(/This looks like a value in/).nth(1)).toBeVisible();
  });

  it("says what a schedule left out falls back to, and where insulin action comes from", async () => {
    render(TherapyPage);

    await expect
      .element(page.getByTestId("therapy-defaults"))
      .toHaveTextContent(
        "Any schedule left out uses Nocturne's built-in default instead. If you use any of these settings, enter your insulin sensitivity and target range as well. How long insulin acts comes from the insulin set in Devices, or 3 hours if none is set."
      );
    await expect.element(page.getByTestId("therapy-defaults").getByRole("link", { name: "Devices" })).toBeVisible();
  });

  it("keeps both target fields when a block's low and high are cleared", async () => {
    render(TherapyPage);
    const low = page.getByRole("spinbutton", { name: "Low from 00:00" });
    const high = page.getByRole("spinbutton", { name: "High from 00:00" });

    await userEvent.fill(low, "5");
    await userEvent.fill(high, "7");
    await userEvent.tab();
    await userEvent.clear(low);
    await userEvent.clear(high);
    await userEvent.tab();

    await expect.element(low).toBeVisible();
    await expect.element(high).toBeVisible();
  });

  it("shows the profile that arrived when saving finds one already there", async () => {
    therapy.enter.mockImplementation(async () => {
      reviewState = reviewOf(TherapySource.Synced, { sourceName: "Loop" });
      throw Object.assign(new Error("A therapy profile already exists."), { status: 409 });
    });
    render(TherapyPage);

    await userEvent.fill(page.getByRole("spinbutton", { name: "Basal rates from 00:00" }), "0.8");
    await userEvent.tab();
    await page.getByRole("button", { name: "Save these settings" }).click();

    await expect.element(page.getByText("This is what Nocturne received from Loop.")).toBeVisible();
    await expect
      .element(page.getByTestId("therapy-conflict"))
      .toHaveTextContent("so what you typed was not saved. Here is what arrived.");
    await expect.element(page.getByText(/already exists/)).not.toBeInTheDocument();
    expect(hubRefresh).not.toHaveBeenCalled();
  });

  it("saves what was typed, in the owner's units, blanks left blank", async () => {
    render(TherapyPage);

    await userEvent.fill(page.getByRole("spinbutton", { name: "Basal rates from 00:00" }), "0.8");
    await userEvent.fill(page.getByRole("spinbutton", { name: "Insulin sensitivity from 00:00" }), "3");
    await userEvent.tab();
    await page.getByRole("button", { name: "Save these settings" }).click();

    await expect.poll(() => therapy.enter.mock.calls.length).toBe(1);
    expect(therapy.enter).toHaveBeenCalledWith({
      glucoseUnits: "mmol",
      basal: [{ time: "00:00", value: 0.8 }],
      carbRatio: [{ time: "00:00", value: undefined }],
      sensitivity: [{ time: "00:00", value: 3 }],
      targetRange: [{ time: "00:00", low: undefined, high: undefined }],
    });
    await expect.poll(() => hubRefresh.mock.calls.length).toBe(1);
  });
});

describe("therapy settings, synced from an app", () => {
  beforeEach(() => {
    reviewState = reviewOf(TherapySource.Synced, { sourceName: "Loop" });
  });

  it("shows what arrived from the app, read-only, in the owner's units", async () => {
    render(TherapyPage);

    await expect.element(page.getByText("This is what Nocturne received from Loop.")).toBeVisible();
    await expect.element(page.getByText("mmol/L/U")).toBeVisible();
    await expect.element(page.getByText("3", { exact: true })).toBeVisible();
    await expect.element(page.getByRole("spinbutton")).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Edit" })).not.toBeInTheDocument();
  });

  it("confirms a match and returns to the hub", async () => {
    render(TherapyPage);

    await page.getByRole("button", { name: "This matches my app" }).click();

    await expect.poll(() => therapy.confirm.mock.calls.length).toBe(1);
    await expect.poll(() => hubRefresh.mock.calls.length).toBe(1);
  });

  it("explains a mismatch is fixed in the app, since edits here would be overwritten", async () => {
    render(TherapyPage);

    await page.getByRole("button", { name: "Something doesn't match" }).click();

    await expect
      .element(page.getByTestId("therapy-mismatch"))
      .toHaveTextContent(/change it in Loop\. If Loop sends its settings again, they replace these, so a change made here would not last\./);
    await expect.element(page.getByTestId("therapy-mismatch")).not.toHaveTextContent(/profile page/);
  });

  it("points a synced profile with no app named to the profile page", async () => {
    reviewState = reviewOf(TherapySource.Synced);
    render(TherapyPage);

    await page.getByRole("button", { name: "Something doesn't match" }).click();

    await expect
      .element(page.getByTestId("therapy-mismatch"))
      .toHaveTextContent("If no app sends your settings to Nocturne, you can find them on the profile page and change the target range there. Basal rates, carb ratios and insulin sensitivity can't be changed in Nocturne yet.");
    await expect
      .element(page.getByTestId("therapy-mismatch").getByRole("link", { name: "profile page" }))
      .toHaveAttribute("href", "/(authenticated)/settings/profile");
  });

  it("offers nothing to confirm once confirmed", async () => {
    reviewState = reviewOf(TherapySource.Synced, { sourceName: "Loop", confirmed: true });
    render(TherapyPage);

    await expect.element(page.getByText("This is what Nocturne received from Loop.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "This matches my app" })).not.toBeInTheDocument();
  });
});

describe("therapy settings, imported from Nightscout", () => {
  it("dates the import and nudges a check against the current app or care-team plan", async () => {
    tenant.relationship = { relationship: "Caregiver", patientName: "Sam" };
    reviewState = reviewOf(TherapySource.Imported, { lastUpdated: "2026-03-01T08:00:00Z" });
    render(TherapyPage);

    await expect.element(page.getByText("This is what Nocturne imported from your Nightscout site.")).toBeVisible();
    await expect.element(page.getByTestId("therapy-last-updated")).toHaveTextContent(/Last updated .*2026/);
    await expect
      .element(page.getByTestId("therapy-review"))
      .toHaveTextContent(
        "Check these against Sam's app today, or the plan Sam's care team gave you."
      );
    await expect.element(page.getByRole("button", { name: "This matches my app" })).toBeVisible();

    await page.getByRole("button", { name: "Something doesn't match" }).click();
    await expect
      .element(page.getByTestId("therapy-mismatch"))
      .toHaveTextContent("If no app sends Sam's settings to Nocturne, you can find them on the profile page and change the target range there. Basal rates, carb ratios and insulin sensitivity can't be changed in Nocturne yet.");
  });
});

describe("therapy settings, entered here", () => {
  it("shows the entered values with a way to the profile page and nothing to confirm", async () => {
    reviewState = reviewOf(TherapySource.Entered);
    render(TherapyPage);

    await expect.element(page.getByText("These are the settings you entered.")).toBeVisible();
    await expect.element(page.getByRole("link", { name: /Open the full profile page/ })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "This matches my app" })).not.toBeInTheDocument();
  });
});
