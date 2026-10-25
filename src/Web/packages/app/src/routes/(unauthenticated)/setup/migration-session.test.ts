import { describe, it, expect, vi, beforeEach } from "vitest";
import { MigrationJobState, MigrationMode } from "$api";
import type { MigrationJobInfo } from "$api";

let history: MigrationJobInfo[] = [];
const startSpy = vi.fn();

// The factory is hoisted above every declaration in this file, so it may only read `history`
// and `startSpy` lazily, from inside the functions it returns.
vi.mock("$api/generated/migrations.generated.remote", () => ({
  getHistory: () => ({ run: () => Promise.resolve(history) }),
  startFromConnector: (connectorName: string) => startSpy(connectorName),
}));

import { startOrResumeMigration } from "./migration-session";

const run = (overrides: Partial<MigrationJobInfo>): MigrationJobInfo => ({
  id: "11111111-1111-1111-1111-111111111111",
  mode: MigrationMode.Api,
  createdAt: "2026-01-04T00:00:00Z",
  sourceDescription: "https://mynightscout.example",
  state: MigrationJobState.Completed,
  startedAt: "2026-01-04T00:00:00Z",
  completedAt: "2026-01-04T00:00:10Z",
  ...overrides,
});

function fakeSessionStorage(): Storage {
  const store = new Map<string, string>();
  return {
    get length() {
      return store.size;
    },
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => void store.set(key, value),
    removeItem: (key: string) => void store.delete(key),
    clear: () => store.clear(),
    key: (index: number) => [...store.keys()].at(index) ?? null,
  } satisfies Storage;
}

describe("startOrResumeMigration", () => {
  beforeEach(() => {
    vi.stubGlobal("sessionStorage", fakeSessionStorage());
    startSpy.mockReset();
    history = [];
  });

  // The tenant's runs outlive its data, so a user who wiped their instance to start over meets a
  // completed run from the last time round. Treating that as this import's own leaves the wizard
  // reporting success over an empty database.
  it("starts an import when the only completed run is from an earlier session", async () => {
    history = [run({ id: "old-job", createdAt: "2025-11-02T00:00:00Z" })];
    startSpy.mockResolvedValue(run({ id: "new-job" }));

    const jobId = await startOrResumeMigration("nightscout");

    expect(startSpy).toHaveBeenCalledWith("nightscout");
    expect(jobId).toBe("new-job");
  });

  it("reuses the completed job this session started rather than importing twice", async () => {
    startSpy.mockResolvedValue(run({ id: "new-job" }));
    const first = await startOrResumeMigration("nightscout");

    history = [run({ id: "new-job" }), run({ id: "old-job" })];
    startSpy.mockClear();

    const second = await startOrResumeMigration("nightscout");

    expect(second).toBe(first);
    expect(startSpy).not.toHaveBeenCalled();
  });

  // Session storage rather than component state is what buys this. The wizard restarts at step
  // one after a reload, so the user walks back into the connect step with no job id in hand.
  it("resumes its own job after the wizard is reloaded", async () => {
    startSpy.mockResolvedValue(run({ id: "new-job" }));
    await startOrResumeMigration("nightscout");

    history = [run({ id: "new-job" })];
    startSpy.mockClear();
    vi.resetModules();
    const reloaded = await import("./migration-session");

    const jobId = await reloaded.startOrResumeMigration("nightscout");

    expect(jobId).toBe("new-job");
    expect(startSpy).not.toHaveBeenCalled();
  });

  it("attaches to a run still in flight whoever started it", async () => {
    history = [
      run({ id: "running-job", state: MigrationJobState.Running }),
      run({ id: "old-job" }),
    ];

    const jobId = await startOrResumeMigration("nightscout");

    expect(jobId).toBe("running-job");
    expect(startSpy).not.toHaveBeenCalled();
  });

  it("starts again after a run this session started failed", async () => {
    startSpy.mockResolvedValue(run({ id: "first-job" }));
    await startOrResumeMigration("nightscout");

    history = [run({ id: "first-job", state: MigrationJobState.Failed })];
    startSpy.mockClear();
    startSpy.mockResolvedValue(run({ id: "second-job" }));

    const jobId = await startOrResumeMigration("nightscout");

    expect(jobId).toBe("second-job");
    expect(startSpy).toHaveBeenCalledOnce();
  });
});
