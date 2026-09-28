import { describe, it, expect, vi, beforeEach } from "vitest";
import { GlucoseStatus } from "$lib/api/generated/nocturne-api-client";
import { currentGlucoseStatus } from "./current-glucose-status.svelte";

const summary = vi.hoisted(() => ({
  current: undefined as
    | { current?: { mills: number; status?: GlucoseStatus } }
    | undefined,
}));

vi.mock("$api/generated/summaries.generated.remote", () => ({
  getSummary: () => summary,
}));

describe("currentGlucoseStatus", () => {
  beforeEach(() => {
    summary.current = undefined;
  });

  it("is the server status when the summary describes the newest reading", () => {
    summary.current = { current: { mills: 1000, status: GlucoseStatus.High } };

    expect(currentGlucoseStatus(1000)).toBe(GlucoseStatus.High);
  });

  it("is undefined while the summary still describes an earlier reading", () => {
    summary.current = { current: { mills: 1000, status: GlucoseStatus.High } };

    expect(currentGlucoseStatus(2000)).toBeUndefined();
  });

  it("is undefined before the summary has loaded or with no reading", () => {
    expect(currentGlucoseStatus(1000)).toBeUndefined();
    expect(currentGlucoseStatus(undefined)).toBeUndefined();
  });
});
