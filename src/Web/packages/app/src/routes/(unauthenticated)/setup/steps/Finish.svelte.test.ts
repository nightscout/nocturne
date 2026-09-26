import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";

import Finish, { type ImportResult, type SourceResult } from "./Finish.svelte";

const noop = () => {};

function renderFinish(
  path: "fresh" | "migration",
  { source = null, importResult = null }: { source?: SourceResult; importResult?: ImportResult } = {}
) {
  return render(Finish, {
    path,
    source,
    importResult,
    onEnterDashboard: noop,
    onNavigateWithCoach: noop,
  });
}

describe("Finish", () => {
  it("does not claim a data source when none was connected", async () => {
    renderFinish("fresh");

    await expect
      .element(page.getByText(/haven't connected a data source yet/))
      .toBeVisible();
    await expect.element(page.getByText(/CGM is connected/)).not.toBeInTheDocument();
    await expect.element(page.getByText(/target range/)).not.toBeInTheDocument();
    await expect.element(page.getByText("Connect a data source")).toBeVisible();
  });

  it("says a saved connector has yet to sync", async () => {
    renderFinish("fresh", { source: "connector-saved" });

    await expect
      .element(page.getByText(/after its first sync/))
      .toBeVisible();
  });

  it("says an uploader is sending only once it is", async () => {
    renderFinish("fresh", { source: "uploader-receiving" });

    await expect
      .element(page.getByText(/is sending readings to Nocturne/))
      .toBeVisible();
  });

  it.each([
    [null, /hasn't been imported yet/],
    ["failed", /didn't finish/],
    ["partial", /not all of it/],
    ["complete", /has been copied into Nocturne/],
  ] as const)("describes a %s import as it ended", async (importResult, lead) => {
    renderFinish("migration", { importResult });

    await expect.element(page.getByText(lead)).toBeVisible();
  });

  it("welcomes the data home only after a complete import", async () => {
    renderFinish("migration", { importResult: "partial" });

    await expect.element(page.getByRole("heading", { name: /You're in/ })).toBeVisible();
    await expect.element(page.getByText(/is home/)).not.toBeInTheDocument();
  });

  it("offers no public share link", async () => {
    renderFinish("fresh");

    await expect.element(page.getByText(/share link/i)).not.toBeInTheDocument();
    await expect.element(page.getByRole("checkbox")).not.toBeInTheDocument();
  });
});
