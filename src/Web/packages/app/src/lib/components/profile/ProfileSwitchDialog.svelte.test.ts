import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";

let create_impl: () => Promise<unknown>;
let create_args: unknown[];

vi.mock("$api/generated/profileActivations.generated.remote", () => ({
  createActivation: (args: unknown) => {
    create_args.push(args);
    return create_impl();
  },
}));

import ProfileSwitchDialog from "./ProfileSwitchDialog.svelte";

function open_dialog() {
  render(ProfileSwitchDialog, { props: { open: true, profileName: "Rhys" } });
}

const record = () => page.getByRole("button", { name: "Record switch" });

describe("ProfileSwitchDialog", () => {
  beforeEach(() => {
    create_impl = () => Promise.resolve({ id: "a" });
    create_args = [];
  });

  it("says the switch is not sent to the pump", async () => {
    open_dialog();

    await expect.element(page.getByText("Switch to Rhys")).toBeVisible();
    await expect.element(page.getByText(/not sent to your pump or to AAPS/)).toBeVisible();
  });

  it("records a permanent switch at 100% with no shift by default", async () => {
    open_dialog();

    await record().click();

    await expect.poll(() => create_args).toEqual([
      { baseProfileName: "Rhys", percentage: 100, timeshiftHours: 0, durationMinutes: null },
    ]);
  });

  it("sends the percentage, shift and a temporary length in minutes", async () => {
    open_dialog();

    await page.getByLabelText("Percentage").fill("110");
    await page.getByLabelText("Timeshift (hours)").fill("-2");
    await page.getByLabelText("Temporary").click();
    await page.getByLabelText("Duration").fill("3");
    await page.getByRole("radio", { name: "Hours" }).click();
    await record().click();

    await expect.poll(() => create_args).toEqual([
      { baseProfileName: "Rhys", percentage: 110, timeshiftHours: -2, durationMinutes: 180 },
    ]);
  });

  it("refuses a percentage outside 30 to 250", async () => {
    open_dialog();

    await page.getByLabelText("Percentage").fill("300");

    await expect.element(page.getByText("Enter a whole number from 30 to 250.")).toBeVisible();
    await expect.element(record()).toBeDisabled();
  });

  it("refuses a shift beyond 23 hours", async () => {
    open_dialog();

    await page.getByLabelText("Timeshift (hours)").fill("24");

    await expect.element(page.getByText("Enter a whole number from -23 to 23.")).toBeVisible();
    await expect.element(record()).toBeDisabled();
  });

  it("refuses a temporary length longer than 7 days", async () => {
    open_dialog();

    await page.getByLabelText("Temporary").click();
    await page.getByLabelText("Duration").fill("169");
    await page.getByRole("radio", { name: "Hours" }).click();

    await expect.element(page.getByText("Enter a length from 1 minute to 7 days.")).toBeVisible();
    await expect.element(record()).toBeDisabled();
  });

  it("shows the server's reason when the switch is refused", async () => {
    create_impl = () => Promise.reject(error(400, "Percentage must be between 30 and 250."));
    open_dialog();

    await record().click();

    await expect.element(page.getByText("Percentage must be between 30 and 250.")).toBeVisible();
  });
});
