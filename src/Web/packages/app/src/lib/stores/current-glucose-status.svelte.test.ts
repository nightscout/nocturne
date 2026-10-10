import { afterEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { refreshSummaryOnNewReading } from "./current-glucose-status.svelte";

const queries = vi.hoisted(() => ({ created: 0 }));

vi.mock("$api/generated/summaries.generated.remote", () => ({
  getSummary: () => {
    queries.created++;
    return summary;
  },
}));

// Reactive, as the real query is: the first load landing is what re-runs the check.
const summary = $state({
  ready: true,
  loading: false,
  current: undefined as { current?: { mills: number } } | undefined,
  refresh: vi.fn(() => Promise.resolve()),
});

const roots: Array<() => void> = [];

/**
 * Each call re-runs the effect even for an equal `mills`, so a repeat reaches
 * the already-described check instead of being absorbed by `$state` equality.
 */
function startWithReading(initial: number | undefined, enabled = true) {
  let reading = $state({ mills: initial });
  roots.push(
    $effect.root(() => {
      refreshSummaryOnNewReading(
        () => reading.mills,
        () => enabled
      );
    })
  );
  flushSync();
  return (mills: number | undefined) => {
    reading = { mills };
    flushSync();
  };
}

function landRefreshFor(mills: number) {
  summary.current = { current: { mills } };
}

afterEach(() => {
  while (roots.length) roots.pop()!();
  summary.ready = true;
  summary.loading = false;
  summary.current = undefined;
  summary.refresh.mockClear();
  queries.created = 0;
});

describe("refreshSummaryOnNewReading", () => {
  it("refreshes once per new reading and never without one", () => {
    landRefreshFor(500);
    const setMills = startWithReading(undefined);
    expect(summary.refresh).toHaveBeenCalledTimes(0);

    setMills(1000);
    expect(summary.refresh).toHaveBeenCalledTimes(1);

    landRefreshFor(1000);
    setMills(1000);
    expect(summary.refresh).toHaveBeenCalledTimes(1);

    setMills(2000);
    expect(summary.refresh).toHaveBeenCalledTimes(2);
  });

  it("does not refresh when the summary already describes the reading", () => {
    landRefreshFor(1000);
    const setMills = startWithReading(undefined);

    setMills(1000);

    expect(summary.refresh).not.toHaveBeenCalled();
  });

  it("leaves the first load as the only request", () => {
    summary.ready = false;
    summary.loading = true;

    startWithReading(1000);

    expect(summary.refresh).not.toHaveBeenCalled();
  });

  it("refreshes for a new reading while an earlier refresh is in flight", () => {
    landRefreshFor(1000);
    const setMills = startWithReading(1000);
    summary.loading = true;

    setMills(2000);

    expect(summary.refresh).toHaveBeenCalledTimes(1);
  });

  it("starts the first load before the store has a reading", () => {
    startWithReading(undefined);

    expect(queries.created).toBeGreaterThan(0);
    expect(summary.refresh).not.toHaveBeenCalled();
  });

  it("starts nothing for a viewer without realtime data", () => {
    startWithReading(1000, false);

    expect(queries.created).toBe(0);
    expect(summary.refresh).not.toHaveBeenCalled();
  });

  it("refreshes when the first load lands describing an older reading", () => {
    summary.ready = false;
    summary.loading = true;
    startWithReading(1000);

    summary.current = { current: { mills: 500 } };
    summary.loading = false;
    summary.ready = true;
    flushSync();

    expect(summary.refresh).toHaveBeenCalledTimes(1);
  });

  it("does not refresh when the first load lands describing the reading", () => {
    summary.ready = false;
    summary.loading = true;
    startWithReading(1000);

    summary.current = { current: { mills: 1000 } };
    summary.loading = false;
    summary.ready = true;
    flushSync();

    expect(summary.refresh).not.toHaveBeenCalled();
  });

  it("retries on the next reading after the first load failed", () => {
    summary.ready = false;
    summary.loading = false;

    startWithReading(1000);

    expect(summary.refresh).toHaveBeenCalledTimes(1);
  });
});
