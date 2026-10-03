import type {} from "@vitest/browser-playwright";
import { render } from "vitest-browser-svelte";
import { cdp, page } from "vitest/browser";
import { afterEach, describe, expect, it, vi } from "vitest";
import PrintReportButton from "./PrintReportButton.svelte";

afterEach(async () => {
  vi.restoreAllMocks();
  await cdp().send("Emulation.setEmulatedMedia", { media: "screen" });
});

describe("PrintReportButton", () => {
  it("says nothing until the print dialog has closed", async () => {
    render(PrintReportButton);

    await expect.element(page.getByRole("button", { name: "Print report" })).toBeVisible();
    await expect.element(page.getByText("Sent to print")).not.toBeInTheDocument();
  });

  it("notes the print was sent once the dialog returns", async () => {
    const print = vi.spyOn(window, "print").mockImplementation(() => {});
    render(PrintReportButton);

    await page.getByRole("button", { name: "Print report" }).click();

    await expect.element(page.getByText("Sent to print")).toBeVisible();
    expect(print).toHaveBeenCalledOnce();
  });

  it("keeps the note off the printed page", async () => {
    vi.spyOn(window, "print").mockImplementation(() => {});
    render(PrintReportButton);
    await page.getByRole("button", { name: "Print report" }).click();
    await expect.element(page.getByText("Sent to print")).toBeVisible();

    await cdp().send("Emulation.setEmulatedMedia", { media: "print" });

    await expect.element(page.getByText("Sent to print")).not.toBeVisible();
  });
});
