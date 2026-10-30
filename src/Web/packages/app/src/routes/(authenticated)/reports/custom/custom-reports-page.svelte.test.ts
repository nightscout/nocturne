import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import type { CustomReportSummary } from "#lib/api/generated/nocturne-api-client.js";

let reports: CustomReportSummary[] = [];
const removed = vi.hoisted(() => [] as unknown[]);

vi.mock("#lib/api/generated/customReports.generated.remote.js", () => ({
  getCustomReports: () => remoteQuery(() => reports),
  deleteCustomReport: remoteCommand((id) => {
    removed.push(id);
  }),
}));

import CustomReportsPage from "./+page.svelte";

describe("custom reports list", () => {
  beforeEach(() => {
    removed.length = 0;
    reports = [
      { id: "a1", name: "Lunch on weekdays", updatedAt: "2026-10-29T10:00:00Z", panelCount: 2, canEdit: false },
      { id: "b2", name: "Nights", updatedAt: "2026-10-28T10:00:00Z", panelCount: 1, canEdit: true },
    ];
  });

  it("lists each saved report with a link to it and its panel count", async () => {
    render(CustomReportsPage, {});

    await expect.element(page.getByRole("link", { name: "Lunch on weekdays" })).toHaveAttribute("href", "/reports/custom/a1");
    await expect.element(page.getByRole("link", { name: "Nights" })).toHaveAttribute("href", "/reports/custom/b2");
    await expect.element(page.getByRole("cell", { name: "2", exact: true })).toBeVisible();
  });

  it("says what is absent, and how to add a report, when there are none", async () => {
    reports = [];
    render(CustomReportsPage, {});

    await expect.element(page.getByText("No custom reports.", { exact: false })).toBeVisible();
    expect(page.getByRole("table").elements()).toHaveLength(0);
  });

  it("offers Delete only on the reports the API says the reader can change", async () => {
    render(CustomReportsPage, {});

    await expect.element(page.getByRole("button", { name: "Delete Nights" })).toBeVisible();
    expect(page.getByRole("button", { name: "Delete Lunch on weekdays" }).elements()).toHaveLength(0);
  });

  it("deletes a report only after the reader confirms", async () => {
    render(CustomReportsPage, {});

    await page.getByRole("button", { name: "Delete Nights" }).click();
    await expect.element(page.getByText('Nocturne will delete "Nights"', { exact: false })).toBeVisible();
    expect(removed).toEqual([]);

    await page.getByRole("button", { name: "Delete", exact: true }).click();
    await vi.waitFor(() => expect(removed).toEqual(["b2"]));
  });
});
