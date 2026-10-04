import { describe, expect, it, vi } from "vitest";
import type { YearSummaryResponse } from "$api/generated/nocturne-api-client";
import { YearLoader } from "./year-loader";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}

describe("year overview loading", () => {
  it("loads both reports through one request and retries partial responses", async () => {
    const response = deferred<YearSummaryResponse>();
    const summary = vi.fn(() => response.promise);
    const events = {
      daily: vi.fn(),
      gri: vi.fn(),
      loading: vi.fn(),
      error: vi.fn(),
    };
    const loader = new YearLoader({ summary }, events);
    loader.reset(["vendor-a"]);
    const run = loader.load(2025);
    expect(loader.load(2025)).toBe(run);
    response.resolve({ dailySummary: { days: [{ totalCarbs: 42 }] } });
    expect(await run).toBe(false);
    expect(summary).toHaveBeenCalledExactlyOnceWith(2025, ["vendor-a"]);
    expect(events.daily).toHaveBeenCalledOnce();
    expect(events.error).toHaveBeenCalledWith(2025, "gri", null);
    expect(loader.canLoad(2025)).toBe(false);
    summary.mockResolvedValue({
      dailySummary: { days: [] },
      griTimeline: { periods: [] },
    });
    expect(await loader.retry(2025)).toBe(true);
    expect(events.gri).toHaveBeenCalledOnce();
  });

  it("suppresses a superseded combined response and reports transport failures for both parts", async () => {
    const response = deferred<YearSummaryResponse>();
    const summary = vi.fn(() => response.promise);
    const events = {
      daily: vi.fn(),
      gri: vi.fn(),
      loading: vi.fn(),
      error: vi.fn(),
    };
    const loader = new YearLoader({ summary }, events);
    const stale = loader.load(2025);
    await Promise.resolve();
    loader.reset(["vendor-b"]);
    response.resolve({
      dailySummary: { days: [] },
      griTimeline: { periods: [] },
    });
    expect(await stale).toBe(false);
    expect(events.daily).not.toHaveBeenCalled();
    expect(events.gri).not.toHaveBeenCalled();
    summary.mockRejectedValue(new Error("offline"));
    expect(await loader.load(2025)).toBe(false);
    expect(events.error.mock.calls.map((call) => call[1])).toEqual([
      "daily",
      "gri",
    ]);
  });

  it("finishes one year before reading another and caches completed results", async () => {
    const response = deferred<YearSummaryResponse>();
    const summary = vi.fn(() => response.promise);
    const events = {
      daily: vi.fn(),
      gri: vi.fn(),
      loading: vi.fn(),
      error: vi.fn(),
    };
    const loader = new YearLoader({ summary }, events);
    const first = loader.load(2025);
    const second = loader.load(2024);
    await Promise.resolve();
    expect(summary).toHaveBeenCalledTimes(1);
    response.resolve({
      dailySummary: { days: [] },
      griTimeline: { periods: [] },
    });
    expect(await first).toBe(true);
    expect(await second).toBe(true);
    expect(await loader.load(2025)).toBe(true);
    expect(summary.mock.calls.map((call) => call[0])).toEqual([2025, 2024]);
  });

  it("drops superseded queued years and keeps outstanding work bounded", async () => {
    const response = deferred<YearSummaryResponse>();
    const summary = vi.fn(() => response.promise);
    const events = {
      daily: vi.fn(),
      gri: vi.fn(),
      loading: vi.fn(),
      error: vi.fn(),
    };
    const loader = new YearLoader({ summary }, events);
    const first = loader.load(2025);
    const obsolete = loader.load(2024);
    await Promise.resolve();
    loader.reset(["vendor-a"]);
    const current = loader.load(2025);
    expect(summary).toHaveBeenCalledOnce();
    response.resolve({
      dailySummary: { days: [{ totalCarbs: 42 }] },
      griTimeline: { periods: [] },
    });
    expect(await first).toBe(false);
    expect(await obsolete).toBe(false);
    expect(await current).toBe(true);
    expect(summary.mock.calls).toEqual([
      [2025, []],
      [2025, ["vendor-a"]],
    ]);
    expect(events.daily).toHaveBeenCalledOnce();
    expect(events.gri).toHaveBeenCalledOnce();
  });

  it("does not publish or start queued work after disposal", async () => {
    const response = deferred<YearSummaryResponse>();
    const summary = vi.fn(() => response.promise);
    const events = {
      daily: vi.fn(),
      gri: vi.fn(),
      loading: vi.fn(),
      error: vi.fn(),
    };
    const loader = new YearLoader({ summary }, events);
    const first = loader.load(2025);
    const queued = loader.load(2024);
    await Promise.resolve();
    loader.dispose();
    response.resolve({
      dailySummary: { days: [] },
      griTimeline: { periods: [] },
    });
    expect(await first).toBe(false);
    expect(await queued).toBe(false);
    expect(summary).toHaveBeenCalledOnce();
    expect(events.daily).not.toHaveBeenCalled();
    expect(events.gri).not.toHaveBeenCalled();
  });

  it("waits for a pending attempt before explicitly retrying", async () => {
    const response = deferred<YearSummaryResponse>();
    const summary = vi.fn(() => response.promise);
    const events = {
      daily: vi.fn(),
      gri: vi.fn(),
      loading: vi.fn(),
      error: vi.fn(),
    };
    const loader = new YearLoader({ summary }, events);
    const first = loader.load(2025);
    await Promise.resolve();
    const retry = loader.retry(2025);
    summary.mockResolvedValue({
      dailySummary: { days: [] },
      griTimeline: { periods: [] },
    });
    response.resolve({ dailySummary: { days: [] } });
    expect(await first).toBe(false);
    expect(await retry).toBe(true);
    expect(summary).toHaveBeenCalledTimes(2);
  });
});
