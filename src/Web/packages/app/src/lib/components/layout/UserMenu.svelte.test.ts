import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";

import Harness from "./UserMenuHarness.test.svelte";

describe("UserMenu", () => {
  it("loads the membership request dialog when it is first opened", async () => {
    render(Harness);

    const dialog = page.getByTestId("request-membership-dialog");
    expect(dialog.elements()).toHaveLength(0);

    await page.getByRole("button", { name: /Guest User/ }).click();
    await page.getByRole("menuitem", { name: "Request Membership" }).click();

    await expect.element(dialog).toBeVisible();
  });
});
