import { describe, it, expect, beforeEach, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: true }));

const { recordApiFailure, readApiFailures, clearApiFailures } = await import(
  "./api-failure-log"
);

function stubRoute(pathname: string) {
  vi.stubGlobal("window", { location: { pathname } });
}

describe("api-failure-log", () => {
  beforeEach(() => {
    clearApiFailures();
    stubRoute("/reports");
    vi.useRealTimers();
  });

  it("records the status, route and sentence the user was shown", () => {
    recordApiFailure(403, "Changing alerts requires alerts.readwrite");

    expect(readApiFailures()).toEqual([
      {
        at: expect.stringMatching(/^\d{4}-\d{2}-\d{2}T/),
        status: 403,
        route: "/reports",
        message: "Changing alerts requires alerts.readwrite",
      },
    ]);
  });

  it("keeps a failure with no status, since not every rejection carries one", () => {
    recordApiFailure(undefined, "Something went wrong");

    expect(readApiFailures()[0].status).toBeUndefined();
  });

  it("collapses the same failure repeated while a surface re-renders", () => {
    for (let i = 0; i < 5; i++) recordApiFailure(500, "Something went wrong");

    expect(readApiFailures()).toHaveLength(1);
  });

  it("keeps the same message from a different route as its own entry", () => {
    recordApiFailure(500, "Something went wrong");
    stubRoute("/settings/connectors");
    recordApiFailure(500, "Something went wrong");

    expect(readApiFailures().map((f) => f.route)).toEqual([
      "/reports",
      "/settings/connectors",
    ]);
  });

  it("records the same failure again once the dedupe window has passed", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-09-11T00:00:00Z"));
    recordApiFailure(500, "Something went wrong");

    vi.setSystemTime(new Date("2026-09-11T00:00:05Z"));
    recordApiFailure(500, "Something went wrong");

    expect(readApiFailures()).toHaveLength(2);
  });

  it("keeps only the most recent failures", () => {
    for (let i = 0; i < 30; i++) recordApiFailure(500, `failure ${i}`);

    const recorded = readApiFailures();
    expect(recorded).toHaveLength(20);
    expect(recorded[0].message).toBe("failure 10");
    expect(recorded[19].message).toBe("failure 29");
  });

  it("truncates a long server sentence so one message cannot fill the buffer", () => {
    recordApiFailure(500, "x".repeat(500));

    const { message } = readApiFailures()[0];
    expect(message).toHaveLength(301);
    expect(message.endsWith("…")).toBe(true);
  });
});

describe("api-failure-log on the server", () => {
  it("records nothing, so one request cannot leak into another's report", async () => {
    vi.resetModules();
    vi.doMock("$app/environment", () => ({ browser: false }));

    const serverSide = await import("./api-failure-log");
    serverSide.recordApiFailure(500, "Something went wrong");

    expect(serverSide.readApiFailures()).toHaveLength(0);
  });
});
