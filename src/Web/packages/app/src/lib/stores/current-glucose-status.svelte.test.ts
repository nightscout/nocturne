import { afterEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { refreshSummaryOnNewReading } from "./current-glucose-status.svelte";

const summary = vi.hoisted(() => ({
  ready: true,
  loading: false,
  current: undefined as { current?: { mills: number } } | undefined,
  refresh: vi.fn(() => Promise.resolve()),
}));

vi.mock("$api/generated/summaries.generated.remote", () => ({
  getSummary: () => summary,
}));

const roots: Array<() => void> = [];

/**
 * Each call re-runs the effect even for an equal `mills`, so a repeat reaches
 * the already-described check instead of being absorbed by `$state` equality.
 */
function startWithReading(initial: number | undefined) {
  let reading = $state({ mills: initial });
  roots.push(
    $effect.root(() => {
      refreshSummaryOnNewReading(() => reading.mills);
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

  it("retries on the next reading after the first load failed", () => {
    summary.ready = false;
    summary.loading = false;

    startWithReading(1000);

    expect(summary.refresh).toHaveBeenCalledTimes(1);
  });
});
