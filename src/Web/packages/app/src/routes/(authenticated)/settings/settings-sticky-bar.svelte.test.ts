import { afterEach, describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import SettingsSaveBarProbe from "./SettingsSaveBarProbe.test.svelte";

const barBottom = () =>
  page.getByText("You have unsaved changes").element().parentElement!.getBoundingClientRect().bottom;

describe("settings layout", () => {
  afterEach(() => window.scrollTo(0, 0));

  it("keeps the connector save bar at the bottom of the viewport while the document scrolls", async () => {
    render(SettingsSaveBarProbe);
    await page.getByRole("textbox").first().fill("b");
    await expect.element(page.getByText("You have unsaved changes")).toBeVisible();

    expect(document.documentElement.scrollHeight).toBeGreaterThan(window.innerHeight);
    expect(barBottom()).toBeCloseTo(window.innerHeight, 0);

    window.scrollTo(0, document.documentElement.scrollHeight / 2);
    await new Promise(requestAnimationFrame);

    expect(window.scrollY).toBeGreaterThan(0);
    expect(barBottom()).toBeCloseTo(window.innerHeight, 0);
  });
});
