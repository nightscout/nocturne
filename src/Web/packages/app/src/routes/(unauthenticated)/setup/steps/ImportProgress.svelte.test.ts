import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { MigrationJobState, MigrationMode, MigrationPhase, PatientRelationship } from "#lib/api/index.js";
import type { MigrationJobInfo, MigrationJobStatus } from "#lib/api/index.js";

let status: MigrationJobStatus;
let history: MigrationJobInfo[] = [];
let statusUnreachable = false;
const statusSpy = vi.fn();

// The factory is hoisted above every declaration here, so it may only reach the bindings
// below from inside the functions it returns.
vi.mock("#lib/api/generated/migrations.generated.remote.js", () => ({
  getStatus: (jobId: string) => {
    statusSpy(jobId);
    return createQueryResource(() =>
      statusUnreachable ? Promise.reject(new Error("unreachable")) : Promise.resolve(status)
    );
  },
  getHistory: () => createQueryResource(() => Promise.resolve(history)),
}));

import ImportProgress from "./ImportProgress.svelte";
import { patientVoice } from "#lib/onboarding/patient-voice.svelte.js";
import { createQueryResource } from "#lib/test-stubs/remote-resource.js";

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
      hasFailures: true,
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
    await expect.element(page.getByTestId("import-progress")).toHaveClass("bg-warning/20");
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
    await expect.element(page.getByTestId("import-progress")).toHaveClass("bg-primary/20");
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

  // The server marks a failed or skipped collection complete, meaning nothing more will be tried.
  it("shows a failed collection with its reason, not as complete", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      hasFailures: true,
      collectionProgress: {
        entries: { collectionName: "entries", isComplete: true, totalDocuments: 3, documentsMigrated: 3 },
        treatments: {
          collectionName: "treatments",
          isComplete: true,
          failureReason: "Nightscout answered with a server error (500).",
        },
      },
    };

    render(ImportProgress, { voice, jobId: "job-7", onComplete: () => {} });

    const failed = page.getByTestId("collection-lane-treatments");
    await expect.element(failed.getByText("Nightscout answered with a server error (500).")).toBeVisible();
    await expect.element(failed.getByLabelText("Failed")).toBeVisible();
    await expect.element(failed.getByLabelText("Complete")).not.toBeInTheDocument();
    await expect
      .element(page.getByTestId("collection-lane-entries").getByLabelText("Complete"))
      .toBeVisible();
  });

  it("shows a skipped collection as skipped, with its reason", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      collectionProgress: {
        subjects: {
          collectionName: "subjects",
          isComplete: true,
          skippedReason: "Skipped: listing the people and devices that can sign in needs an admin API secret.",
        },
      },
    };

    render(ImportProgress, { voice, jobId: "job-8", onComplete: () => {} });

    const lane = page.getByTestId("collection-lane-subjects");
    await expect.element(lane.getByText("Access tokens")).toBeVisible();
    await expect.element(lane.getByText("skipped", { exact: true })).toBeVisible();
    await expect.element(lane.getByText(/needs an admin API secret/)).toBeVisible();
    await expect.element(lane.getByLabelText("Complete")).not.toBeInTheDocument();
  });

  it("says how many records of a collection could not be imported", async () => {
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      collectionProgress: {
        entries: { collectionName: "entries", isComplete: true, totalDocuments: 10, documentsMigrated: 8, documentsFailed: 2 },
      },
    };

    render(ImportProgress, { voice, jobId: "job-9", onComplete: () => {} });

    await expect.element(page.getByText("2 records could not be imported.")).toBeVisible();
  });

  // A source that gives no count has no total to measure against until the pull ends.
  it("shows no percentage for a collection in flight that has no total", async () => {
    status = {
      state: MigrationJobState.Running,
      progressPercentage: 0,
      phase: MigrationPhase.Importing,
      currentCollection: "entries",
      collectionProgress: {
        entries: { collectionName: "entries", isComplete: false, totalDocuments: 0, documentsMigrated: 12400, progressPercentage: 0 },
      },
    };

    render(ImportProgress, { voice, jobId: "job-10", onComplete: () => {} });

    const lane = page.getByTestId("collection-lane-entries");
    await expect.element(lane.getByText("12,400 records")).toBeVisible();
    await expect.element(lane.getByLabelText("In progress")).toBeVisible();
    await expect.element(lane.getByText("0%")).not.toBeInTheDocument();
  });

  it.each([
    ["00:03:00", "About 3 min remaining"],
    ["1.02:03:04", "About 26 h 3 min remaining"],
    ["00:00:20.5000000", "Less than 1 minute remaining"],
  ])("words the time remaining %s", async (estimatedTimeRemaining, expected) => {
    status = {
      state: MigrationJobState.Running,
      progressPercentage: 40,
      phase: MigrationPhase.Importing,
      estimatedTimeRemaining,
      collectionProgress: {},
    };

    render(ImportProgress, { voice, jobId: `job-eta-${estimatedTimeRemaining}`, onComplete: () => {} });

    await expect.element(page.getByText(expected)).toBeVisible();
  });

  it("says what the run is doing before it has a collection to show", async () => {
    status = {
      state: MigrationJobState.Running,
      progressPercentage: 0,
      phase: MigrationPhase.Counting,
      collectionProgress: {},
    };

    render(ImportProgress, { voice, jobId: "job-11", onComplete: () => {} });

    await expect.element(page.getByText("Counting the records in Nightscout")).toBeVisible();
  });

  it.each([
    [MigrationJobState.Cancelled, "The import was canceled."],
    [MigrationJobState.Interrupted, /The import stopped because the server restarted/],
  ] as const)("words a run that ended %s", async (state, expected) => {
    status = {
      state,
      progressPercentage: 10,
      collectionProgress: { entries: { collectionName: "entries", documentsMigrated: 5 } },
    };

    render(ImportProgress, { voice, jobId: `job-ended-${state}`, onComplete: () => {} });

    await expect.element(page.getByText(expected)).toBeVisible();
    await expect.element(page.getByTestId("import-progress")).toHaveClass("bg-warning/20");
  });

  // The summary of what failed has to stay up until it is read; Finish has no way back to it.
  it("does not move on by itself from a run that completed with a failed collection", async () => {
    status = { state: MigrationJobState.Running, progressPercentage: 99, collectionProgress: {} };
    const onComplete = vi.fn();
    const onSettled = vi.fn();

    render(ImportProgress, { voice, jobId: "job-12", onSettled, onComplete });
    await expect.poll(() => statusSpy.mock.calls.length).toBeGreaterThan(0);
    status = {
      state: MigrationJobState.Completed,
      progressPercentage: 100,
      hasFailures: true,
      errorMessage: "1 of 2 collections imported, 1 failed.",
      collectionProgress: {},
    };

    await expect.poll(() => onSettled.mock.calls.length, { timeout: 10000 }).toBe(1);
    await new Promise((resolve) => setTimeout(resolve, 5000));
    expect(onComplete).not.toHaveBeenCalled();
  }, 20000);

  it("moves on by itself from a run that completed in full", async () => {
    status = { state: MigrationJobState.Running, progressPercentage: 99, collectionProgress: {} };
    const onComplete = vi.fn();

    render(ImportProgress, { voice, jobId: "job-13", onComplete });
    await expect.poll(() => statusSpy.mock.calls.length).toBeGreaterThan(0);
    status = { state: MigrationJobState.Completed, progressPercentage: 100, collectionProgress: {} };

    await expect.poll(() => onComplete.mock.calls.length, { timeout: 15000 }).toBe(1);
  }, 20000);

  // Finish states what the import achieved, so the wizard has to learn how it ended.
  it.each([
    ["complete", MigrationJobState.Completed, false],
    ["partial", MigrationJobState.Completed, true],
    ["failed", MigrationJobState.Failed, true],
  ] as const)("reports a %s run and stops blocking", async (expected, state, hasFailures) => {
    status = { state, progressPercentage: 100, hasFailures, collectionProgress: {} };
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

  // The older history can take hours. Once the recent data is in, the step stops watching and
  // hands the run to the app.
  it("hands a live run over once its recent data is in", async () => {
    status = {
      state: MigrationJobState.Running,
      progressPercentage: 12,
      recentDataReady: true,
      collectionProgress: {},
    };
    const onResult = vi.fn();
    const onSettled = vi.fn();
    const onRecentReady = vi.fn();

    render(ImportProgress, {
      voice,
      jobId: "job-recent",
      onResult,
      onSettled,
      onRecentReady,
      onComplete: () => {},
    });

    await expect.poll(() => onRecentReady.mock.calls).toEqual([["job-recent"]]);
    expect(onResult.mock.calls).toEqual([["running"], ["background"]]);
    expect(onSettled).toHaveBeenCalledTimes(1);
    expect(statusSpy).toHaveBeenCalledTimes(1);
  });

  it("keeps watching a live run whose recent data is not in yet", async () => {
    status = {
      state: MigrationJobState.Running,
      progressPercentage: 3,
      recentDataReady: false,
      collectionProgress: {},
    };
    const onRecentReady = vi.fn();
    const onSettled = vi.fn();

    render(ImportProgress, { voice, jobId: "job-early", onSettled, onRecentReady, onComplete: () => {} });

    await expect.poll(() => statusSpy.mock.calls.length).toBeGreaterThan(0);
    expect(onRecentReady).not.toHaveBeenCalled();
    expect(onSettled).not.toHaveBeenCalled();
  });

  it("stops blocking when there is no run to follow", async () => {
    const onSettled = vi.fn();

    render(ImportProgress, { voice, onSettled, onComplete: () => {} });

    await expect.poll(() => onSettled.mock.calls.length).toBe(1);
    await expect.element(page.getByText(/No import from Nightscout is running/)).toBeVisible();
    await expect.element(page.getByText(/Nocturne is copying/)).not.toBeInTheDocument();
    await expect.element(page.getByText(/records imported/)).not.toBeInTheDocument();
  });

  it("says why no run could be started instead of showing an empty import", async () => {
    render(ImportProgress, { voice, startError: "Your Nightscout site refused the request.", onComplete: () => {} });

    await expect
      .element(page.getByText("We could not start the import from Nightscout. You can continue and run it later from Settings."))
      .toBeVisible();
    await expect.element(page.getByText("Your Nightscout site refused the request.", { exact: true })).toBeVisible();
    await expect.element(page.getByText(/records imported/)).not.toBeInTheDocument();
  });

  it("stops blocking once status polling is lost, reporting the run as possibly still running", async () => {
    statusUnreachable = true;
    const onResult = vi.fn();
    const onSettled = vi.fn();

    render(ImportProgress, { voice, jobId: "job-lost", onResult, onSettled, onComplete: () => {} });

    await expect.poll(() => onSettled.mock.calls.length, { timeout: 15000 }).toBe(1);
    expect(onResult.mock.calls).toEqual([["running"]]);
    await expect.element(page.getByText(/It may still be running. Check Settings/)).toBeVisible();
  }, 20000);
});
