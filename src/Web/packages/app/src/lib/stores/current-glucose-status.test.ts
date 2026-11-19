import { describe, it, expect, vi, beforeEach } from "vitest";
import { GlucoseStatus } from "#lib/api/generated/nocturne-api-client.js";
import {
  calculatedCob,
  calculatedIob,
  currentGlucoseStatus,
} from "./current-glucose-status.svelte";

const summary = vi.hoisted(() => ({
  current: undefined as
    | {
        current?: { mills: number; status?: GlucoseStatus };
        iob?: number;
        iobKnown?: boolean;
        cob?: number;
        cobKnown?: boolean;
      }
    | undefined,
}));

vi.mock("#lib/api/generated/summaries.generated.remote.js", () => ({
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

describe("calculatedIob and calculatedCob", () => {
  beforeEach(() => {
    summary.current = undefined;
  });

  it("are the server values when the summary marks them known, including a real zero", () => {
    summary.current = { iob: 0, iobKnown: true, cob: 12, cobKnown: true };

    expect(calculatedIob()).toBe(0);
    expect(calculatedCob()).toBe(12);
  });

  it("are undefined when the summary marks them unknown, so a sent 0 is not shown", () => {
    summary.current = { iob: 0, iobKnown: false, cob: 0, cobKnown: false };

    expect(calculatedIob()).toBeUndefined();
    expect(calculatedCob()).toBeUndefined();
  });

  it("are undefined before the summary has loaded", () => {
    expect(calculatedIob()).toBeUndefined();
    expect(calculatedCob()).toBeUndefined();
  });
});
