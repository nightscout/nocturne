import { describe, expect, it, vi } from "vitest";

const getTeam = vi.hoisted(() => vi.fn());

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({ getTeam }));

import { load } from "./+page.js";

function eventFor(parentData: { refusedAsDemoSubject?: boolean } = {}) {
  return {
    params: { teamId: "t1" },
    parent: async () => parentData,
  } as unknown as Parameters<typeof load>[0];
}

describe("team page load", () => {
  it("lets a 404 for the team reach the error page instead of rendering the page", async () => {
    const notFound = { status: 404, body: { message: "Not found" } };
    getTeam.mockReset();
    getTeam.mockRejectedValue(notFound);

    await expect(load(eventFor())).rejects.toBe(notFound);
    expect(getTeam).toHaveBeenCalledWith("t1");
  });

  it("loads for a team the person is in", async () => {
    getTeam.mockReset();
    getTeam.mockResolvedValue({ id: "t1" });

    await expect(load(eventFor())).resolves.toBeUndefined();
  });

  it("asks the API for nothing when it refuses the viewer as the demo subject", async () => {
    getTeam.mockReset();

    await expect(load(eventFor({ refusedAsDemoSubject: true }))).resolves.toBeUndefined();
    expect(getTeam).not.toHaveBeenCalled();
  });
});
