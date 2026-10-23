import { describe, it, expect } from "vitest";
import { MigrationJobState, type MigrationJobInfo } from "$api";
import { completedCleanly } from "./migration-outcome";

const job = (overrides: Partial<MigrationJobInfo>) =>
  ({ state: MigrationJobState.Completed, hasFailures: false, ...overrides }) as MigrationJobInfo;

describe("completedCleanly", () => {
  it("celebrates a run that completed without a failed collection", () => {
    expect(completedCleanly(job({}))).toBe(true);
  });

  it("celebrates a skip-only run, the ordinary hosted import", () => {
    expect(completedCleanly(job({ errorMessage: "6 of 7 collections imported, 1 skipped." }))).toBe(true);
  });

  it("does not celebrate a completed run with a failed collection", () => {
    expect(completedCleanly(job({ hasFailures: true, errorMessage: "1 of 2 collections imported, 1 failed." }))).toBe(false);
  });

  it.each([
    MigrationJobState.Failed,
    MigrationJobState.Cancelled,
    MigrationJobState.Interrupted,
    MigrationJobState.Running,
  ])("does not celebrate a %s run", (state) => {
    expect(completedCleanly(job({ state }))).toBe(false);
  });

  it("does not celebrate a run missing from history", () => {
    expect(completedCleanly(undefined)).toBe(false);
  });
});
