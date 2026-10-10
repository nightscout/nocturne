import { describe, expect, it } from "vitest";
import {
  isEntryDocument,
  parseDataUpdate,
  parseNotification,
  parseSyncProgress,
  parseTrackerUpdate,
} from "./payloads";

describe("isEntryDocument", () => {
  it("accepts a record whose keyed fields are absent, null or correctly typed", () => {
    expect(isEntryDocument({})).toBe(true);
    expect(isEntryDocument({ _id: null, mills: null, sgv: null })).toBe(true);
    expect(isEntryDocument({ _id: "a", mills: 1, sgv: 100, extra: [] })).toBe(true);
  });

  it("rejects non-records and mistyped keyed fields", () => {
    expect(isEntryDocument(null)).toBe(false);
    expect(isEntryDocument([])).toBe(false);
    expect(isEntryDocument("entry")).toBe(false);
    expect(isEntryDocument({ _id: 1 })).toBe(false);
    expect(isEntryDocument({ mills: "1" })).toBe(false);
    expect(isEntryDocument({ sgv: "100" })).toBe(false);
  });
});

describe("parseDataUpdate", () => {
  it("wraps a single document and drops the malformed ones from an array", () => {
    expect(parseDataUpdate({ sgv: 100 })).toEqual([{ sgv: 100 }]);
    expect(parseDataUpdate([{ sgv: 100 }, { sgv: "x" }, null])).toEqual([{ sgv: 100 }]);
    expect(parseDataUpdate("nope")).toEqual([]);
  });
});

describe("parseNotification", () => {
  it("accepts a record with a string id", () => {
    const notification = { id: "n1", title: "t" };
    expect(parseNotification(notification)).toBe(notification);
  });

  it("rejects a missing or non-string id and non-records", () => {
    expect(parseNotification({ title: "t" })).toBeNull();
    expect(parseNotification({ id: 1 })).toBeNull();
    expect(parseNotification([{ id: "n1" }])).toBeNull();
    expect(parseNotification(null)).toBeNull();
  });
});

describe("parseTrackerUpdate", () => {
  it("accepts a known action with an identifiable instance", () => {
    const instance = { id: "i1", name: "Sensor" };
    expect(parseTrackerUpdate({ action: "complete", instance })).toEqual({
      action: "complete",
      instance,
    });
  });

  it("rejects an unknown action or an instance without an id", () => {
    expect(parseTrackerUpdate({ action: "explode", instance: { id: "i1" } })).toBeNull();
    expect(parseTrackerUpdate({ action: "create", instance: { id: "" } })).toBeNull();
    expect(parseTrackerUpdate({ action: "create", instance: [] })).toBeNull();
    expect(parseTrackerUpdate({ action: "create" })).toBeNull();
    expect(parseTrackerUpdate(null)).toBeNull();
  });
});

describe("parseSyncProgress", () => {
  const complete = {
    connectorId: "dexcom",
    connectorName: "Dexcom",
    phase: "Syncing",
    errorMessage: "boom",
    timestamp: "2026-01-01T00:00:00Z",
    messageType: "FetchingData",
    messageParams: { dataType: "Entries", count: "3" },
  };

  it("passes a complete event through", () => {
    expect(parseSyncProgress(complete)).toEqual(complete);
  });

  it("accepts every phase and every message type", () => {
    for (const phase of ["Syncing", "Completed", "Failed"]) {
      expect(parseSyncProgress({ ...complete, phase })?.phase).toBe(phase);
    }
    for (const messageType of [
      "Authenticating",
      "FetchingData",
      "ProcessingDataType",
      "PublishingDataType",
      "SyncComplete",
      "SyncFailed",
    ]) {
      expect(parseSyncProgress({ ...complete, messageType })?.messageType).toBe(messageType);
    }
  });

  it("accepts an empty connector id", () => {
    expect(parseSyncProgress({ ...complete, connectorId: "" })?.connectorId).toBe("");
  });

  it("drops fields outside the event", () => {
    expect(parseSyncProgress({ ...complete, tenantId: "t1" })).toEqual(complete);
  });

  it("defaults the optional fields when they are missing", () => {
    expect(parseSyncProgress({ connectorId: "dexcom", phase: "Completed" })).toEqual({
      connectorId: "dexcom",
      connectorName: "",
      phase: "Completed",
      errorMessage: null,
      timestamp: "",
      messageType: null,
      messageParams: null,
    });
  });

  it("keeps explicit nulls on the nullable fields", () => {
    const event = parseSyncProgress({
      ...complete,
      errorMessage: null,
      messageType: null,
      messageParams: null,
    });
    expect(event?.errorMessage).toBeNull();
    expect(event?.messageType).toBeNull();
    expect(event?.messageParams).toBeNull();
  });

  it("defaults the optional fields when they are mistyped", () => {
    expect(
      parseSyncProgress({
        connectorId: "dexcom",
        connectorName: 1,
        phase: "Failed",
        errorMessage: { text: "boom" },
        timestamp: 1700000000000,
        messageType: "Rebooting",
        messageParams: { count: 3 },
      })
    ).toEqual({
      connectorId: "dexcom",
      connectorName: "",
      phase: "Failed",
      errorMessage: null,
      timestamp: "",
      messageType: null,
      messageParams: null,
    });
  });

  it("rejects message params that are not a string record", () => {
    expect(parseSyncProgress({ ...complete, messageParams: ["a"] })?.messageParams).toBeNull();
    expect(parseSyncProgress({ ...complete, messageParams: "a=b" })?.messageParams).toBeNull();
    expect(
      parseSyncProgress({ ...complete, messageParams: { a: "b", c: null } })?.messageParams
    ).toBeNull();
  });

  it("accepts empty message params", () => {
    expect(parseSyncProgress({ ...complete, messageParams: {} })?.messageParams).toEqual({});
  });

  it("rejects a missing or mistyped connector id", () => {
    const { connectorId: _, ...withoutId } = complete;
    expect(parseSyncProgress(withoutId)).toBeNull();
    expect(parseSyncProgress({ ...complete, connectorId: 1 })).toBeNull();
    expect(parseSyncProgress({ ...complete, connectorId: null })).toBeNull();
  });

  it("rejects a missing or unknown phase", () => {
    const { phase: _, ...withoutPhase } = complete;
    expect(parseSyncProgress(withoutPhase)).toBeNull();
    expect(parseSyncProgress({ ...complete, phase: "Paused" })).toBeNull();
    expect(parseSyncProgress({ ...complete, phase: "syncing" })).toBeNull();
  });

  it("rejects non-objects", () => {
    expect(parseSyncProgress(null)).toBeNull();
    expect(parseSyncProgress(undefined)).toBeNull();
    expect(parseSyncProgress("dexcom")).toBeNull();
    expect(parseSyncProgress([complete])).toBeNull();
  });
});
