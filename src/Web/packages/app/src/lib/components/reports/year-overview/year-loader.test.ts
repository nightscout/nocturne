import { describe, expect, it, vi } from "vitest";
import type {
  DailySummaryResponse,
  GriTimelineResponse,
} from "$api/generated/nocturne-api-client";
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

function setup() {
  const daily = deferred<DailySummaryResponse>();
  const gri = deferred<GriTimelineResponse>();
  const requests = {
    daily: vi.fn<
      (year: number, sources: string[]) => Promise<DailySummaryResponse>
    >(() => daily.promise),
    gri: vi.fn<
      (year: number, sources: string[]) => Promise<GriTimelineResponse>
    >(() => gri.promise),
  };
  const events = {
    daily: vi.fn(),
    gri: vi.fn(),
    loading: vi.fn(),
    error: vi.fn(),
  };
  return {
    loader: new YearLoader(requests, events),
    requests,
    events,
    daily,
    gri,
  };
}

describe("year overview loading", () => {
  it("retries after both parts of a failed parallel request finish", async () => {
    const { loader, requests, daily, gri } = setup();
    const first = loader.load(2025);
    await Promise.resolve();
    daily.reject(new Error("unavailable"));
    await Promise.resolve();
    requests.daily.mockResolvedValue({ days: [{ totalCarbs: 45 }] });
    const retry = loader.retry(2025);
    expect(requests.daily).toHaveBeenCalledOnce();
    gri.resolve({ periods: [] });
    expect(await first).toBe(false);
    expect(await retry).toBe(true);
    expect(requests.daily).toHaveBeenCalledTimes(2);
  });
  it("starts daily and GRI together and shares repeated year requests", async () => {
    const { loader, requests, daily, gri, events } = setup();
    const first = loader.load(2025);
    expect(loader.load(2025)).toBe(first);
    await Promise.resolve();
    expect(requests.daily).toHaveBeenCalledOnce();
    expect(requests.gri).toHaveBeenCalledOnce();
    daily.resolve({
      year: 2025,
      days: [{ date: "2025-01-01", totalCarbs: 45, totalDailyDose: 32 }],
    });
    await vi.waitFor(() => expect(events.daily).toHaveBeenCalledOnce());
    expect(loader.busy).toBe(true);
    gri.resolve({ year: 2025, periods: [] });
    expect(await first).toBe(true);
    expect(await loader.load(2025)).toBe(true);
    expect(requests.daily).toHaveBeenCalledOnce();
    expect(events.daily.mock.calls[0][1].days[0].totalCarbs).toBe(45);
  });

  it("finishes one year before reading another", async () => {
    const { loader, requests, daily, gri } = setup();
    const first = loader.load(2025);
    const second = loader.load(2024);
    await Promise.resolve();
    expect(requests.daily).toHaveBeenCalledTimes(1);
    daily.resolve({ days: [] });
    await Promise.resolve();
    expect(requests.daily).toHaveBeenCalledTimes(1);
    gri.resolve({ periods: [] });
    await first;
    await second;
    expect(requests.daily.mock.calls.map((call) => call[0])).toEqual([
      2025, 2024,
    ]);
  });

  it("drops superseded queued years and never publishes an old filter response", async () => {
    const { loader, requests, events, daily, gri } = setup();
    const old = loader.load(2025);
    const obsoleteYear = loader.load(2024);
    await Promise.resolve();
    loader.reset(["vendor-a"]);
    const current = loader.load(2025);
    daily.resolve({ days: [{ totalCarbs: 99 }] });
    gri.resolve({ periods: [] });
    expect(await old).toBe(false);
    expect(await obsoleteYear).toBe(false);
    expect(await current).toBe(true);
    expect(requests.daily.mock.calls).toEqual([
      [2025, []],
      [2025, ["vendor-a"]],
    ]);
    expect(events.daily).toHaveBeenCalledTimes(1);
    expect(events.gri).toHaveBeenCalledTimes(1);
    expect(
      events.loading.mock.calls.filter((call) => call[1] === false)
    ).toEqual([[2025, false]]);
  });

  it("keeps a successful daily response when GRI fails without retrying on every intersection", async () => {
    const { loader, daily, gri, requests, events } = setup();
    const run = loader.load(2025);
    daily.resolve({ days: [{ totalBolusUnits: 4 }] });
    gri.reject(new Error("unavailable"));
    expect(await run).toBe(false);
    expect(events.daily).toHaveBeenCalledOnce();
    expect(events.error).toHaveBeenCalledWith(2025, "gri", expect.any(Error));
    expect(await loader.load(2025)).toBe(false);
    expect(loader.canLoad(2025)).toBe(false);
    expect(requests.gri).toHaveBeenCalledOnce();
    await loader.retry(2025);
    expect(requests.gri).toHaveBeenCalledTimes(2);
  });

  it("does not publish or start queued work after leaving the page", async () => {
    const { loader, requests, events, daily, gri } = setup();
    const active = loader.load(2025);
    const queued = loader.load(2024);
    await Promise.resolve();
    loader.dispose();
    daily.resolve({ days: [] });
    gri.resolve({ periods: [] });
    await Promise.all([active, queued]);
    expect(events.daily).not.toHaveBeenCalled();
    expect(events.gri).not.toHaveBeenCalled();
    expect(requests.daily).toHaveBeenCalledOnce();
    expect(await loader.load(2023)).toBe(false);
  });
});
