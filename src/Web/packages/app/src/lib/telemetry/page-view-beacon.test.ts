import { beforeEach, describe, expect, it, vi } from "vitest";

const env = vi.hoisted(() => ({ enabled: false }));
const recordPageView = vi.hoisted(() => vi.fn(() => Promise.resolve()));

vi.mock("$app/env/public", () => ({
  get PUBLIC_PAGE_COUNTS_ENABLED() {
    return env.enabled;
  },
}));
vi.mock("#lib/api/generated/pageViews.generated.remote.js", () => ({ recordPageView }));

import { createPageViewBeacon } from "./page-view-beacon";

const routeId = "/(authenticated)/reports/agp";

describe("page-view beacon", () => {
  beforeEach(() => {
    env.enabled = false;
    recordPageView.mockClear();
  });

  it("sends no beacon while page counts are off", () => {
    createPageViewBeacon().arrived(routeId);

    expect(recordPageView).not.toHaveBeenCalled();
  });

  it("sends the route id alone when page counts are on", () => {
    env.enabled = true;

    createPageViewBeacon().arrived(routeId);

    expect(recordPageView).toHaveBeenCalledExactlyOnceWith({ routeId });
  });

  it("counts the first page of a visit once when both the layout mount and the enter navigation report it", () => {
    const beacon = createPageViewBeacon(true);

    beacon.arrived("/(authenticated)");
    beacon.arrived("/(authenticated)");

    expect(recordPageView).toHaveBeenCalledExactlyOnceWith({ routeId: "/(authenticated)" });
  });

  it("counts the first page of a visit when only the layout mount reports it", () => {
    createPageViewBeacon(true).arrived("/(authenticated)");

    expect(recordPageView).toHaveBeenCalledExactlyOnceWith({ routeId: "/(authenticated)" });
  });

  it("counts each page a visitor moves to", () => {
    const beacon = createPageViewBeacon(true);

    beacon.arrived("/(authenticated)");
    beacon.arrived(routeId);
    beacon.arrived("/(authenticated)");

    expect(recordPageView.mock.calls).toEqual([
      [{ routeId: "/(authenticated)" }],
      [{ routeId }],
      [{ routeId: "/(authenticated)" }],
    ]);
  });

  it("does not count a navigation that stays on the same page", () => {
    const beacon = createPageViewBeacon(true);

    beacon.arrived("/(authenticated)/alerts/[id]");
    beacon.arrived("/(authenticated)/alerts/[id]");

    expect(recordPageView).toHaveBeenCalledOnce();
  });

  it("does not count a page with no route, such as a 404, and counts the next page", () => {
    const beacon = createPageViewBeacon(true);

    beacon.arrived(null);
    beacon.arrived(undefined);
    expect(recordPageView).not.toHaveBeenCalled();

    beacon.arrived("/(authenticated)");
    beacon.arrived(null);
    beacon.arrived("/(authenticated)");
    expect(recordPageView).toHaveBeenCalledTimes(2);
  });

  it("drops a failed send", async () => {
    recordPageView.mockReturnValueOnce(Promise.reject(new Error("offline")));

    expect(() => createPageViewBeacon(true).arrived("/(authenticated)")).not.toThrow();
    await Promise.resolve();
  });
});

describe("PUBLIC_PAGE_COUNTS_ENABLED", () => {
  it("is on only for the exact value true", async () => {
    const { variables } = await import("../../env");
    const parse = variables.PUBLIC_PAGE_COUNTS_ENABLED.schema["~standard"].validate;

    for (const [value, on] of [[undefined, false], ["", false], ["false", false], ["1", false], ["TRUE", false], ["true", true]] as const) {
      expect(await parse(value)).toEqual({ value: on });
    }
  });
});
