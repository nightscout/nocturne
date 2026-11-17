import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import TabsHarness from "./TabsHarness.test.svelte";

const underlineCanvas = (tab: Element) => tab.querySelector("span[aria-hidden='true'] canvas");

describe("Tabs.Trigger painted underline", () => {
  it("paints under the selected tab only", async () => {
    render(TabsHarness);

    const first = page.getByRole("tab", { name: "First" });
    const second = page.getByRole("tab", { name: "Second" });
    await expect.element(first).toHaveAttribute("aria-selected", "true");

    expect(underlineCanvas(first.element())).not.toBeNull();
    expect(underlineCanvas(second.element())).toBeNull();
  });

  it("moves to the tab that becomes selected", async () => {
    render(TabsHarness);

    const first = page.getByRole("tab", { name: "First" });
    const second = page.getByRole("tab", { name: "Second" });
    await second.click();

    await expect.element(second).toHaveAttribute("aria-selected", "true");
    await expect.element(page.getByText("Second panel")).toBeVisible();
    expect(underlineCanvas(second.element())).not.toBeNull();
    expect(underlineCanvas(first.element())).toBeNull();
  });
});
