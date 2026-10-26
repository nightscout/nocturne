import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it, vi } from "vitest";
import TreatmentCategoryTabs from "./TreatmentCategoryTabs.svelte";

const counts = {
  all: 12,
  bolus: 4,
  carbs: 3,
  bgCheck: 2,
  note: 1,
  deviceEvent: 1,
  basalInjection: 1,
};

describe("TreatmentCategoryTabs", () => {
  const original: [number, number] = [window.innerWidth, window.innerHeight];

  afterEach(async () => {
    await page.viewport(original[0], original[1]);
  });

  it("shows every category as a tile with its count", async () => {
    render(TreatmentCategoryTabs, {
      activeCategory: "all",
      categoryCounts: counts,
      onChange: () => {},
    });

    const tabs = Array.from(
      document.querySelectorAll<HTMLElement>("[role='tab']")
    );
    expect(tabs).toHaveLength(7);
    expect(
      document
        .querySelector("[data-slot='tabs-list']")
        ?.getAttribute("data-variant")
    ).toBe("tiles");
    expect(getComputedStyle(tabs[0]).flexDirection).toBe("column");
    await expect
      .element(page.getByRole("tab", { name: /Insulin/ }))
      .toHaveTextContent("4");
  });

  it("keeps every tile inside the page at phone width", async () => {
    await page.viewport(360, 780);
    render(TreatmentCategoryTabs, {
      activeCategory: "all",
      categoryCounts: counts,
      onChange: () => {},
    });

    const tabs = Array.from(
      document.querySelectorAll<HTMLElement>("[role='tab']")
    );
    for (const tab of tabs) {
      expect(
        tab.getBoundingClientRect().right,
        tab.textContent ?? ""
      ).toBeLessThanOrEqual(360);
    }
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(
      window.innerWidth
    );
  });

  it("reports the category the user picks", async () => {
    const onChange = vi.fn();
    render(TreatmentCategoryTabs, {
      activeCategory: "all",
      categoryCounts: counts,
      onChange,
    });

    await page.getByRole("tab", { name: /Carbs/ }).click();

    expect(onChange).toHaveBeenCalledWith("carbs");
  });
});
