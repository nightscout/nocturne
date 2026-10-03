import { describe, it, expect } from "vitest";
import { isErrorStatus, presentConnection } from "./connection-indicator.svelte";

describe("isErrorStatus", () => {
  it("reports a dropped or failed socket", () => {
    expect(isErrorStatus("disconnected")).toBe(true);
    expect(isErrorStatus("error")).toBe(true);
  });

  it("stays quiet before a connection has been attempted", () => {
    expect(isErrorStatus("idle")).toBe(false);
  });

  it("stays quiet while a connection is in flight", () => {
    expect(isErrorStatus("connecting")).toBe(false);
    expect(isErrorStatus("reconnecting")).toBe(false);
    expect(isErrorStatus("connected")).toBe(false);
  });

  it("does not treat a denied realtime session as a fault", () => {
    expect(isErrorStatus("unauthorized")).toBe(false);
  });
});

describe("presentConnection", () => {
  it("shows a drop still inside the grace window as pending, never as a failure", () => {
    expect(presentConnection("disconnected", false)).toBe("pending");
    expect(presentConnection("error", false)).toBe("pending");
    expect(presentConnection("idle", false)).toBe("pending");
  });

  it("shows a reported outage as unavailable", () => {
    expect(presentConnection("disconnected", true)).toBe("unavailable");
    expect(presentConnection("connecting", true)).toBe("unavailable");
  });

  it("shows a denied session as not live, whatever was reported", () => {
    expect(presentConnection("unauthorized", false)).toBe("denied");
    expect(presentConnection("unauthorized", true)).toBe("denied");
  });

  it("shows a connected socket as live", () => {
    expect(presentConnection("connected", false)).toBe("live");
  });
});
