import { describe, it, expect } from "vitest";
import { parseAlertDispatchEvent } from "./parse-dispatch.js";

const payload = {
  alertType: null,
  ruleName: "Urgent low",
  glucoseValue: 54,
  trend: null,
  trendRate: null,
  readingTimestamp: "2026-01-01T00:00:00.000Z",
  excursionId: "018f2a1b-3c4d-7000-8000-000000000001",
  instanceId: "018f2a1b-3c4d-7000-8000-000000000002",
  tenantId: "018f2a1b-3c4d-7000-8000-a1b2c3d4e5f6",
  subjectName: "Alex",
  activeExcursionCount: 1,
  severity: "critical",
};

const event = { deliveryId: "d-1", channelType: "discord_dm", destination: "c-1", tenantSlug: "acme", payload };

const parse = (over: Record<string, unknown>) => parseAlertDispatchEvent({ ...event, payload: { ...payload, ...over } });

describe("parseAlertDispatchEvent", () => {
  it("reads a dispatch the API sends, with its nulls as absent values", () => {
    const parsed = parseAlertDispatchEvent(event);

    expect(parsed?.payload.ruleName).toBe("Urgent low");
    expect(parsed?.payload.glucoseValue).toBe(54);
    expect(parsed?.payload.trend).toBeUndefined();
    expect(parsed?.payload.alertType).toBeUndefined();
  });

  it("delivers a severity this build does not know", () => {
    expect(parse({ severity: "emergency" })?.payload.severity).toBe("emergency");
  });

  it("delivers an alert type this build does not know", () => {
    expect(parse({ alertType: "glucose_variability" })?.payload.alertType).toBe("glucose_variability");
  });

  it("delivers a payload kind this build does not know", () => {
    expect(parse({ kind: "digest" })?.payload.kind).toBe("digest");
  });

  it("reads a held notice's facts, keeping only the pairs that are objects", () => {
    const parsed = parse({
      kind: "held",
      heldFacts: [{ fact: "glucose", reason: "stale" }, "pump", { fact: 3, reason: "absent" }],
    });

    expect(parsed?.payload.heldFacts).toEqual([
      { fact: "glucose", reason: "stale" },
      { fact: undefined, reason: "absent" },
    ]);
  });

  it("reads held facts that are not a list as none", () => {
    expect(parse({ heldFacts: "glucose" })?.payload.heldFacts).toBeUndefined();
  });

  it("reads what met the condition, keeping each field only when it has its type", () => {
    const parsed = parse({
      metBy: [{ leafId: 0, kind: "predicted", curve: "zt", offsetMinutes: 25, value: 62.5 }, "x", { kind: 3, value: "12" }],
    });

    expect(parsed?.payload.metBy).toEqual([
      { leafId: 0, kind: "predicted", curve: "zt", offsetMinutes: 25, value: 62.5 },
      { leafId: undefined, kind: undefined, curve: undefined, offsetMinutes: undefined, value: undefined },
    ]);
  });

  it("reads the limit a condition compared with and the fact it came from", () => {
    const parsed = parse({
      metBy: [{ leafId: 0, kind: "threshold", value: 66, limit: 70, limitFact: "target_low", limitOffset: -10 }, { kind: "threshold", value: 76, limit: "80", limitFact: 1, limitOffset: "x" }],
    });

    expect(parsed?.payload.metBy?.[0]).toMatchObject({ limit: 70, limitFact: "target_low", limitOffset: -10 });
    expect(parsed?.payload.metBy?.[1]?.limitOffset).toBeUndefined();
    expect(parsed?.payload.metBy?.[1]?.limit).toBeUndefined();
    expect(parsed?.payload.metBy?.[1]?.limitFact).toBeUndefined();
  });

  it("delivers a payload with a field this build does not know", () => {
    expect(parse({ batteryLevel: 40 })).toBeDefined();
  });

  it("delivers a timestamp with no offset", () => {
    expect(parse({ readingTimestamp: "2026-01-01T00:00:00" })?.payload.readingTimestamp).toBe("2026-01-01T00:00:00");
  });

  it("delivers ids that are not RFC 9562 GUIDs", () => {
    const id = "33333333-3333-3333-3333-333333333333";

    expect(parse({ excursionId: id, tenantId: id })?.payload.tenantId).toBe(id);
  });

  it.each(["excursionId", "tenantId"])("refuses a payload with no %s", (name) => {
    const { [name]: _omitted, ...rest } = payload as Record<string, unknown>;

    expect(parseAlertDispatchEvent({ ...event, payload: rest })).toBeUndefined();
  });

  it.each(["ruleName", "subjectName", "readingTimestamp"])("delivers a payload with no %s", (name) => {
    const { [name]: _omitted, ...rest } = payload as Record<string, unknown>;

    expect(parseAlertDispatchEvent({ ...event, payload: rest })?.payload).toMatchObject({ [name]: "" });
  });

  it("delivers a payload whose rule name is not a string", () => {
    expect(parse({ ruleName: 7 })?.payload.ruleName).toBe("");
  });

  it.each(["deliveryId", "channelType", "destination", "tenantSlug"])("refuses an event with no %s", (name) => {
    const { [name]: _omitted, ...rest } = event as Record<string, unknown>;

    expect(parseAlertDispatchEvent(rest)).toBeUndefined();
  });

  it("refuses a body that is not an object", () => {
    expect(parseAlertDispatchEvent(null)).toBeUndefined();
    expect(parseAlertDispatchEvent("alert")).toBeUndefined();
  });
});
