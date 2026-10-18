import { describe, expect, it, vi } from "vitest";
import { printReport, ReportPrintContext } from "./report-print.svelte";

describe("report print preparation", () => {
  it("cancels printing when the prepared report unmounts during layout settling", async () => {
    const print = vi.spyOn(window, "print").mockImplementation(() => {});
    const context = new ReportPrintContext();
    const dispose = context.providePreparation(async () => {});
    const version = context.preparationVersion;
    try {
      const run = printReport(
        () => context.prepare(),
        () => context.preparationVersion === version
      );
      await vi.waitFor(() =>
        expect(
          document.documentElement.style.getPropertyValue(
            "--report-print-width"
          )
        ).not.toBe("")
      );
      dispose();
      await expect(run).rejects.toThrow("Report changed before printing");
      expect(print).not.toHaveBeenCalled();
      expect(
        document.documentElement.style.getPropertyValue("--report-print-width")
      ).toBe("");
    } finally {
      print.mockRestore();
    }
  });

  it("does not open the print dialog when preparation fails", async () => {
    const print = vi.spyOn(window, "print").mockImplementation(() => {});
    try {
      await expect(
        printReport(async () => {
          throw new Error("Incomplete year");
        })
      ).rejects.toThrow("Incomplete year");
      expect(print).not.toHaveBeenCalled();
    } finally {
      print.mockRestore();
    }
  });
});
