import { describe, it, expect, beforeEach, vi } from "vitest";
import { flushIdle, resetIdle } from "$lib/test-stubs/when-idle";

const load = vi.hoisted(() => vi.fn());

vi.mock("$lib/utils/when-idle", () => import("$lib/test-stubs/when-idle"));

vi.mock("$lib/api/generated/coachMarks.generated.remote", async () => {
  const { createQueryResource } = await import("$lib/test-stubs/remote-resource");
  return {
    getAll: () => createQueryResource(() => load()),
    updateStatus: vi.fn(() => Promise.resolve()),
    deleteAll: vi.fn(() => Promise.resolve()),
  };
});

import { createCoachMarkAdapter } from "./adapter";

describe("createCoachMarkAdapter", () => {
  beforeEach(() => {
    resetIdle();
    localStorage.clear();
    load.mockReset();
    load.mockResolvedValue([
      { id: "m1", markKey: "quick-tour.chart", status: "seen", seenAt: "2026-10-01T00:00:00Z" },
    ]);
  });

  it("reads the stored marks only once the page is idle", async () => {
    const adapter = createCoachMarkAdapter();

    const states = adapter.fetchAll();
    await Promise.resolve();

    expect(load).not.toHaveBeenCalled();

    flushIdle();

    await expect(states).resolves.toEqual([
      {
        id: "m1",
        markKey: "quick-tour.chart",
        status: "seen",
        seenAt: "2026-10-01T00:00:00Z",
        completedAt: null,
      },
    ]);
    expect(load).toHaveBeenCalledOnce();
  });

  it("never asks the server when it keeps marks locally", async () => {
    const adapter = createCoachMarkAdapter(true);

    await expect(adapter.fetchAll()).resolves.toEqual([]);
    flushIdle();

    expect(load).not.toHaveBeenCalled();
  });
});
