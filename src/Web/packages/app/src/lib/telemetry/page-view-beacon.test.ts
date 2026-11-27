import { beforeEach, describe, expect, it, vi } from "vitest";

const env = vi.hoisted(() => ({ enabled: false }));
const recordPageView = vi.hoisted(() => vi.fn(() => Promise.resolve()));

vi.mock("$app/env/public", () => ({
  get PUBLIC_PAGE_COUNTS_ENABLED() {
    return env.enabled;
  },
}));
vi.mock("#lib/api/generated/pageViews.generated.remote.js", () => ({ recordPageView }));

import { sendPageView } from "./page-view-beacon";

const page = (id: string | null) => ({ route: { id } }) as never;

describe("page-view beacon", () => {
  beforeEach(() => {
    env.enabled = false;
    recordPageView.mockClear();
  });

  it("sends no beacon while page counts are off", () => {
    sendPageView({ from: null, to: page("/(authenticated)/reports/agp") });

    expect(recordPageView).not.toHaveBeenCalled();
  });

  it("sends the route id alone when page counts are on", () => {
    env.enabled = true;

    sendPageView({ from: page("/(authenticated)"), to: page("/(authenticated)/reports/agp") });

    expect(recordPageView).toHaveBeenCalledExactlyOnceWith({ routeId: "/(authenticated)/reports/agp" });
  });

  it("counts the first page of a visit", () => {
    sendPageView({ from: null, to: page("/(authenticated)") }, true);

    expect(recordPageView).toHaveBeenCalledExactlyOnceWith({ routeId: "/(authenticated)" });
  });

  it("does not count a navigation that stays on the same page", () => {
    sendPageView({ from: page("/(authenticated)/alerts/[id]"), to: page("/(authenticated)/alerts/[id]") }, true);

    expect(recordPageView).not.toHaveBeenCalled();
  });

  it("does not count a page with no route, such as a 404", () => {
    sendPageView({ from: page("/(authenticated)"), to: page(null) }, true);
    sendPageView({ from: page("/(authenticated)"), to: null }, true);

    expect(recordPageView).not.toHaveBeenCalled();
  });

  it("drops a failed send", async () => {
    recordPageView.mockReturnValueOnce(Promise.reject(new Error("offline")));

    expect(() => sendPageView({ from: null, to: page("/(authenticated)") }, true)).not.toThrow();
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
