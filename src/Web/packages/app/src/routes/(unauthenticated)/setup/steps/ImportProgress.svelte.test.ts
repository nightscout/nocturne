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
    };

    render(ImportProgress, { jobId: "job-1", onComplete: () => {} });

    await expect
      .element(page.getByText(/1 of 7 collections imported/))
      .toBeVisible();
  });

  // A read-only API secret cannot list sign-ins, which is how most Nightscout sites are set up.
  // The wizard says so, but colouring it as a warning would alarm almost everyone who imports.
  it("reports a skipped collection without the warning colour", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      errorMessage:
        "6 of 7 collections imported, 1 skipped. Skipped: listing the people and devices that can sign in needs an admin API secret.",
      collectionProgress: {
        subjects: {
          collectionName: "subjects",
          isComplete: true,
          skippedReason: "Skipped: listing the people and devices that can sign in needs an admin API secret.",
        },
      },
    };

    render(ImportProgress, { jobId: "job-3", onComplete: () => {} });

    const summary = page.getByText(/6 of 7 collections imported/);
    await expect.element(summary).toBeVisible();
    await expect.element(summary).not.toHaveClass("text-warning");
  });

  it("colours the summary as a warning when a collection actually failed", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      errorMessage: "1 of 2 collections imported, 1 failed. treatments: Nightscout answered with a server error (500). It may be down or restarting; try again shortly.",
      collectionProgress: {
        treatments: {
          collectionName: "treatments",
          isComplete: true,
          failureReason: "Nightscout answered with a server error (500). It may be down or restarting; try again shortly.",
        },
      },
    };

    render(ImportProgress, { jobId: "job-4", onComplete: () => {} });

    await expect
      .element(page.getByText(/1 of 2 collections imported/))
      .toHaveClass("text-warning");
  });

  it("says nothing extra when every collection imported", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      errorMessage: undefined,
      collectionProgress: {},
    };

    render(ImportProgress, { jobId: "job-2", onComplete: () => {} });

    await expect.element(page.getByText(/collections imported/)).not.toBeInTheDocument();
  });

  // Someone who deleted a stretch of readings and re-imports it gets none of them back. The lane
  // has to say so beside the count, or the import reads as having restored them.
  it("shows the records a collection skipped beside its count", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      collectionProgress: {
        entries: {
          collectionName: "entries",
          isComplete: true,
          totalDocuments: 4210,
          documentsMigrated: 4209,
          documentsSkippedUnsupported: 1,
          recordsSkippedDeleted: 412,
        },
      },
    };

    render(ImportProgress, { jobId: "job-5", onComplete: () => {} });

    await expect
      .element(page.getByText(/412 records were not added again/))
      .toBeVisible();
    await expect
      .element(page.getByText(/1 record was not added because Nocturne does not store/))
      .toBeVisible();
  });

  // A collection whose every document was dealt with is finished even when some were of a kind
  // Nocturne does not store; the lane must not stall short of complete.
  it("shows the server's percentage for a lane rather than stored over total", async () => {
    status = {
      state: MigrationJobState.Running,
      progressPercentage: 50,
      collectionProgress: {
        entries: {
          collectionName: "entries",
          isComplete: false,
          totalDocuments: 10,
          documentsMigrated: 4,
          documentsSkippedUnsupported: 3,
          progressPercentage: 70,
        },
      },
    };

    render(ImportProgress, { jobId: "job-6", onComplete: () => {} });

    await expect.element(page.getByText("70%", { exact: true })).toBeVisible();
  });
});
