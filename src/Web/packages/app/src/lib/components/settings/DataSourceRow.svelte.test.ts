import { describe, it, expect } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";

import DataSourceRow from "./DataSourceRow.svelte";

describe("DataSourceRow", () => {
  it("labels the count with the window the API reports", async () => {
    render(DataSourceRow, {
      name: "Rig",
      icon: undefined,
      status: "active",
      totalEntries: 1200,
      totalEntriesWindowDays: 14,
    });

    await expect
      .element(page.getByText("records in the last 14 days"))
      .toBeInTheDocument();
  });

  it("labels a count with no window as a total", async () => {
    render(DataSourceRow, {
      name: "Rig",
      icon: undefined,
      status: "active",
      totalEntries: 1200,
      totalEntriesWindowDays: null,
    });

    await expect.element(page.getByText("records in total")).toBeInTheDocument();
    await expect.element(page.getByText("in the last")).not.toBeInTheDocument();
  });

  it("shows no count when the API sent none", async () => {
    render(DataSourceRow, {
      name: "Rig",
      icon: undefined,
      status: "disabled",
      totalEntries: undefined,
    });

    await expect.element(page.getByText("records")).not.toBeInTheDocument();
  });
});
