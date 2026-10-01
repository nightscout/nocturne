import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import Harness from "./nested-in-popover.test.svelte";

// DndPanel's menu lives inside the notifications popover.
describe("DropdownMenu inside a Popover", () => {
  it("selects an item without dismissing the popover", async () => {
    const onselect = vi.fn();
    render(Harness, { onselect });

    await page.getByRole("button", { name: "Notifications" }).click();
    await page.getByRole("button", { name: "Do Not Disturb" }).click();
    await page.getByRole("menuitem", { name: "30 minutes" }).click();

    expect(onselect).toHaveBeenCalledOnce();
    await expect.element(page.getByText("Popover body")).toBeVisible();
  });
});
