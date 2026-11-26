import { describe, expect, it } from "vitest";
import { AlertConditionType } from "@nocturne/api-client";
import type { AlertPayload } from "../types.js";
import { decodeActionValue } from "../lib/action-value.js";
import { trendArrow } from "../lib/format.js";
import { alertTemplateMessage, readTemplatePayload, templatePayload } from "./alert-template.js";

const TENANT = "11111111-1111-1111-1111-111111111111";
const EXCURSION = "33333333-3333-3333-3333-333333333333";

const payload = (overrides: Partial<AlertPayload> = {}): AlertPayload => ({
  tenantId: TENANT,
  excursionId: EXCURSION,
  ruleName: "Urgent low",
  subjectName: "Alex",
  glucoseValue: 54,
  trend: "SingleDown",
  severity: "critical",
  readingTimestamp: "2026-11-26T03:00:00Z",
  ...overrides,
});

const TEMPLATE = { name: "nocturne_alert", language: "en_US" };

function bodyOf(message: ReturnType<typeof alertTemplateMessage>): string[] {
  const body = message.components?.find((c) => c.type === "body");
  return (body?.parameters ?? []).map((p) => (p.type === "text" ? p.text : ""));
}

describe("alertTemplateMessage", () => {
  it("fills the body with the person, the alert's name and the reading with its unit and trend", () => {
    const message = alertTemplateMessage(TEMPLATE, payload());

    expect(message).toMatchObject({ name: "nocturne_alert", language: "en_US" });
    expect(bodyOf(message)).toEqual(["Alex", "Urgent low", `54 mg/dL ${trendArrow("SingleDown")}`]);
  });

  it("says there is no reading for an alert about readings stopping", () => {
    const message = alertTemplateMessage(
      TEMPLATE,
      payload({ glucoseValue: undefined, trend: undefined, alertType: AlertConditionType.SignalLoss }),
    );

    expect(bodyOf(message)[2]).toBe("no reading");
  });

  it("keeps each parameter to what Meta accepts: no newline or tab, at most four spaces in a row, never empty", () => {
    const message = alertTemplateMessage(TEMPLATE, payload({ subjectName: "Alex\nSmith", ruleName: "Low\t\tat      night" }));

    expect(bodyOf(message).slice(0, 2)).toEqual(["Alex Smith", "Low at    night"]);
    expect(bodyOf(alertTemplateMessage(TEMPLATE, payload({ ruleName: " " })))[1]).toBe("-");
  });

  it("gives the quick reply the Acknowledge button's action, for the alert's tenant and excursion", () => {
    const button = alertTemplateMessage(TEMPLATE, payload()).components?.find((c) => c.type === "button");
    const parameter = button?.type === "button" ? button.parameters[0] : undefined;
    const action = parameter?.type === "payload" ? readTemplatePayload(parameter.payload) : null;

    expect(action?.actionId).toBe("ack_alert");
    expect(decodeActionValue(action!.value).excursionId).toBe(EXCURSION);
  });
});

describe("readTemplatePayload", () => {
  it("reads back what templatePayload wrote", () => {
    expect(readTemplatePayload(templatePayload("ack_alert", "abc:def"))).toEqual({
      actionId: "ack_alert",
      value: "abc:def",
    });
  });

  it.each(["Acknowledge", "", "nocturne|", "nocturne||value"])("reads nothing from %j", (payload) => {
    expect(readTemplatePayload(payload)).toBeNull();
  });
});
