import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import Harness from "./ReportsFilterSidebar.test-harness.svelte";

describe("ReportsFilterSidebar", () => {
  it("seeds the default range's preset and dates from the resolved range", async () => {
    render(Harness);

    const preset = page.getByRole("button", { name: "14 Days" });
    await expect.element(preset.first()).toBeVisible();
    expect(preset.first().element().className).toContain("bg-primary");
    expect(
      page.getByRole("button", { name: "7 Days" }).first().element().className
    ).not.toContain("bg-primary");

    await expect
      .element(page.getByText("Select dates"))
      .not.toBeInTheDocument();
  });

  it("seeds an explicit custom range and selects no preset", async () => {
    render(Harness, {
      props: { customFrom: "2026-01-01", customTo: "2026-01-10" },
    });

    const fourteenDays = page.getByRole("button", { name: "14 Days" });
    await expect.element(fourteenDays.first()).toBeVisible();
    expect(fourteenDays.first().element().className).not.toContain(
      "bg-primary"
    );

    await expect
      .element(page.getByText("Select dates"))
      .not.toBeInTheDocument();
  });

  it("shows a report's filters below the range, starting them on open and writing them on Apply", async () => {
    const log: string[] = [];
    render(Harness, { props: { extensionLog: log } });

    await expect.element(page.getByLabelText("Report filter", { exact: true })).toBeVisible();
    expect(log).toEqual(["begin"]);

    await page.getByRole("button", { name: "Apply Filters" }).click();
    await expect.poll(() => log).toEqual(["begin", "apply"]);
  });

  it("clears a report's filters on Reset", async () => {
    const log: string[] = [];
    render(Harness, { props: { extensionLog: log } });

    await page.getByRole("button", { name: "Reset" }).click();
    await expect.poll(() => log).toEqual(["begin", "reset"]);
  });
});
