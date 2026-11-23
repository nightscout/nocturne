import { describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import PermissionCategorySelector from "./PermissionCategorySelector.svelte";

async function openGlucoseMenu() {
  await page.getByText("Read", { exact: true }).click();
}

const disabled = (name: string) => expect.element(page.getByRole("option", { name, exact: true })).toHaveAttribute("data-disabled");
const enabled = (name: string) =>
  expect.element(page.getByRole("option", { name, exact: true })).not.toHaveAttribute("data-disabled");

describe("PermissionCategorySelector with refused and covered atoms", () => {
  it("disables a level holding a refused atom and leaves the others", async () => {
    render(PermissionCategorySelector, { selected: ["glucose.read"], refusedAtoms: ["glucose.readwrite"] });

    await openGlucoseMenu();

    await disabled("Read & write");
    await enabled("Read");
    await enabled("No access");
  });

  it("disables a level holding an atom outside the covered list, and keeps narrowing possible", async () => {
    render(PermissionCategorySelector, {
      selected: ["glucose.read", "glucose.readwrite"],
      coveredAtoms: ["glucose.read", "glucose.readwrite"],
    });

    await page.getByText("Read & write", { exact: true }).click();

    await enabled("Read & write");
    await enabled("Read");
    await enabled("No access");
  });

  it("disables a level whose atoms the covered list does not hold", async () => {
    render(PermissionCategorySelector, { selected: ["glucose.read"], coveredAtoms: ["glucose.read"] });

    await openGlucoseMenu();

    await disabled("Read & write");
    await enabled("Read");
    await enabled("No access");
  });

  it("disables nothing when neither list is given", async () => {
    render(PermissionCategorySelector, { selected: ["glucose.read"] });

    await openGlucoseMenu();

    await enabled("Read & write");
  });
});
