import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";

import ProfileIconPicker from "./ProfileIconPicker.svelte";

describe("ProfileIconPicker", () => {
  it("renders its trigger as one button that opens the picker", async () => {
    render(ProfileIconPicker, { selectedIcon: "heart" });

    const trigger = page.getByRole("button", { name: "Heart" });
    await expect.element(trigger).toBeVisible();
    expect(trigger.getByRole("button").elements()).toHaveLength(0);

    await trigger.click();

    await expect.element(trigger).toHaveAttribute("aria-expanded", "true");
    await expect.element(page.getByText("Select an icon")).toBeVisible();
  });
});
