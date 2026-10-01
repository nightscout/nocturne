import { describe, it, expect } from "vitest";
import { isErrorStatus } from "./connection-indicator.svelte";

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
