import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { MigrationJobState } from "$api";
import type { MigrationJobStatus } from "$api";

let status: MigrationJobStatus;

vi.mock("$api/generated/migrations.generated.remote", () => ({
  getStatus: () => ({ run: () => Promise.resolve(status) }),
}));

import ImportProgress from "./ImportProgress.svelte";

describe("ImportProgress", () => {
  // A run that imported some collections and was refused others still ends Completed — there is
  // no partial state — so the server's summary is the only thing standing between the wizard and
  // presenting a half-finished import as a clean one.
  it("shows the server's summary when a completed run imported only part of the data", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      errorMessage:
        "1 of 7 collections imported, 1 failed, 5 not attempted. treatments: Could not reach your Nightscout server.",
      collectionProgress: {},
    } as MigrationJobStatus;

    render(ImportProgress, { jobId: "job-1", onComplete: () => {} });

    await expect
      .element(page.getByText(/1 of 7 collections imported/))
      .toBeVisible();
  });

  it("says nothing extra when every collection imported", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      errorMessage: null,
      collectionProgress: {},
    } as MigrationJobStatus;

    render(ImportProgress, { jobId: "job-2", onComplete: () => {} });

    await expect.element(page.getByText(/collections imported/)).not.toBeInTheDocument();
  });
});
