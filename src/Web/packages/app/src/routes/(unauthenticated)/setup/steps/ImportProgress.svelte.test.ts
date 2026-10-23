import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { MigrationJobState, MigrationMode, PatientRelationship } from "$api";
import type { MigrationJobInfo, MigrationJobStatus } from "$api";

let status: MigrationJobStatus;
let history: MigrationJobInfo[] = [];
let statusUnreachable = false;
const statusSpy = vi.fn();

// The factory is hoisted above every declaration here, so it may only reach the bindings
// below from inside the functions it returns.
vi.mock("$api/generated/migrations.generated.remote", () => ({
  getStatus: (jobId: string) => {
    statusSpy(jobId);
    return {
      run: () =>
        statusUnreachable ? Promise.reject(new Error("unreachable")) : Promise.resolve(status),
    };
  },
  getHistory: () => ({ run: () => Promise.resolve(history) }),
}));

import ImportProgress from "./ImportProgress.svelte";
import { patientVoice } from "$lib/onboarding/patient-voice.svelte";

const voice = patientVoice({ relationship: PatientRelationship.Self });

describe("ImportProgress", () => {
  beforeEach(() => {
    // The module under test keeps this session's job id in session storage, which a real
    // browser carries between tests in this file.
    sessionStorage.clear();
    statusSpy.mockClear();
    history = [];
    statusUnreachable = false;
  });

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

    render(ImportProgress, { voice, jobId: "job-1", onComplete: () => {} });

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

    render(ImportProgress, { voice, jobId: "job-3", onComplete: () => {} });

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

    render(ImportProgress, { voice, jobId: "job-4", onComplete: () => {} });

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

    render(ImportProgress, { voice, jobId: "job-2", onComplete: () => {} });

    await expect.element(page.getByText(/collections imported/)).not.toBeInTheDocument();
  });

  // Migration runs outlive the data they imported, so a tenant that wiped its instance to start
  // over still has completed runs on file. Watching one of those would report an import that
  // this session never made — the wizard knows of no job, and says so.
  it("ignores a completed run this session did not start", async () => {
    history = [
      {
        id: "old-job",
        mode: MigrationMode.Api,
        createdAt: "2025-11-02T00:00:00Z",
        state: MigrationJobState.Completed,
        completedAt: "2025-11-02T00:04:00Z",
      },
    ];
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      collectionProgress: {},
    };
    const onProgressChange = vi.fn();

    render(ImportProgress, { voice, onProgressChange, onComplete: () => {} });

    await expect.element(page.getByText(/No import from Nightscout is running/)).toBeVisible();
    expect(statusSpy).not.toHaveBeenCalled();
    expect(onProgressChange).not.toHaveBeenCalled();
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

    render(ImportProgress, { voice, jobId: "job-5", onComplete: () => {} });

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

    render(ImportProgress, { voice, jobId: "job-6", onComplete: () => {} });

    await expect.element(page.getByText("70%", { exact: true })).toBeVisible();
  });

  // Finish states what the import achieved, so the wizard has to learn how it ended.
  it.each([
    ["complete", MigrationJobState.Completed, {}],
    [
      "partial",
      MigrationJobState.Completed,
      { treatments: { collectionName: "treatments", isComplete: true, failureReason: "Could not reach your Nightscout server." } },
    ],
    ["failed", MigrationJobState.Failed, {}],
  ] as const)("reports a %s run and stops blocking", async (expected, state, collectionProgress) => {
    status = { state, progressPercentage: 100, collectionProgress };
    const onResult = vi.fn();
    const onSettled = vi.fn();

    render(ImportProgress, { voice, jobId: `job-${expected}`, onResult, onSettled, onComplete: () => {} });

    await expect.poll(() => onResult.mock.calls).toEqual([[expected]]);
    expect(onSettled).toHaveBeenCalledOnce();
  });

  it("reports a live run as running and keeps blocking", async () => {
    status = { state: MigrationJobState.Running, progressPercentage: 20, collectionProgress: {} };
    const onResult = vi.fn();
    const onSettled = vi.fn();

    render(ImportProgress, { voice, jobId: "job-live", onResult, onSettled, onComplete: () => {} });

    await expect.poll(() => onResult.mock.calls).toEqual([["running"]]);
    expect(onSettled).not.toHaveBeenCalled();
  });

  it("stops blocking when there is no run to follow", async () => {
    const onSettled = vi.fn();

    render(ImportProgress, { voice, onSettled, onComplete: () => {} });

    await expect.poll(() => onSettled.mock.calls.length).toBe(1);
    await expect.element(page.getByText(/No import from Nightscout is running/)).toBeVisible();
    await expect.element(page.getByText(/We're streaming/)).not.toBeInTheDocument();
    await expect.element(page.getByText(/records migrated/)).not.toBeInTheDocument();
  });

  it("says why no run could be started instead of showing an empty import", async () => {
    render(ImportProgress, { voice, startError: "Your Nightscout site refused the request.", onComplete: () => {} });

    await expect
      .element(page.getByText("We couldn't start the import from Nightscout. You can continue and run it later from Settings."))
      .toBeVisible();
    await expect.element(page.getByText("Your Nightscout site refused the request.", { exact: true })).toBeVisible();
    await expect.element(page.getByText(/records migrated/)).not.toBeInTheDocument();
  });

  it("stops blocking once status polling is lost, reporting the run as possibly still running", async () => {
    statusUnreachable = true;
    const onResult = vi.fn();
    const onSettled = vi.fn();

    render(ImportProgress, { voice, jobId: "job-lost", onResult, onSettled, onComplete: () => {} });

    await expect.poll(() => onSettled.mock.calls.length, { timeout: 15000 }).toBe(1);
    expect(onResult.mock.calls).toEqual([["running"]]);
    await expect.element(page.getByText(/It may still be running; check Settings/)).toBeVisible();
  }, 20000);
});
