import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ProfileEditDialog from "./ProfileEditDialog.svelte";

function makeProfile(over: Record<string, unknown> = {}) {
  return {
    id: "p1",
    defaultProfile: "Weekday",
    units: "mg/dl",
    store: {
      Weekday: {
        units: "mg/dl",
        timezone: "UTC",
        dia: 4,
        carbs_hr: 20,
        basal: [{ time: "00:00", value: 0.8 }],
        carbratio: [{ time: "00:00", value: 10 }],
        sens: [{ time: "00:00", value: 50 }],
        target_low: [{ time: "00:00", value: 90 }],
        target_high: [{ time: "00:00", value: 120 }],
      },
    },
    ...over,
  };
}

function renderDialog(profile: ReturnType<typeof makeProfile> | null, storeName: string | null) {
  render(ProfileEditDialog, {
    props: { open: true, profile, storeName, onClose: vi.fn(), onSave: vi.fn() },
  });
}

describe("ProfileEditDialog", () => {
  it("titles the dialog with the store being edited", async () => {
    renderDialog(makeProfile(), "Weekday");

    await expect.element(page.getByRole("heading", { name: "Edit profile: Weekday" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Save changes" })).toBeVisible();
  });

  it("falls back to the default profile name when no store is named", async () => {
    renderDialog(makeProfile(), null);

    await expect.element(page.getByRole("heading", { name: "Edit profile: Weekday" })).toBeVisible();
  });

  it("says no profile is selected when given none", async () => {
    renderDialog(null, null);

    await expect.element(page.getByRole("heading", { name: "Edit profile: Unknown" })).toBeVisible();
    await expect.element(page.getByText("No profile selected for editing.")).toBeVisible();
  });

  it("warns before editing an externally managed profile", async () => {
    renderDialog(makeProfile({ isExternallyManaged: true }), "Weekday");

    await expect.element(page.getByRole("heading", { name: "Externally managed profile" })).toBeVisible();
    await expect.element(page.getByText(/managed by an external source/)).toBeVisible();

    await page.getByRole("button", { name: "I understand" }).click();
    await expect.element(page.getByRole("heading", { name: "Externally managed profile" })).not.toBeInTheDocument();
  });

  it("does not warn for a profile Nocturne manages", async () => {
    renderDialog(makeProfile(), "Weekday");

    await expect.element(page.getByRole("heading", { name: "Edit profile: Weekday" })).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "Externally managed profile" })).not.toBeInTheDocument();
  });
});
