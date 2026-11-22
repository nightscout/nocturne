import { describe, it, expect } from "vitest";
import { AcknowledgedCard, ActiveAlertsCard, AlertCard } from "./alert.js";
import {
  cardButtons,
  cardFields,
  cardStyles,
  cardTexts,
  cardTitle,
} from "./card.test-utils.js";
import { decodeActionValue, encodeTenantKey } from "../lib/action-value.js";
import type { ActiveExcursionResponse } from "@nocturne/api-client";
import type { AlertPayload } from "../types.js";

const TENANT = "018f2a1b-3c4d-7000-8000-a1b2c3d4e5f6";
const EXCURSION = "33333333-3333-3333-3333-333333333333";

/**
 * Mirrors `encodeTelegramCallbackData` in `@chat-adapter/telegram`
 * (`dist/index.js`), which the package does not export: it prefixes `chat:` to
 * `JSON.stringify({ a: actionId, v: value })` and throws above 64 bytes, failing
 * the whole delivery.
 */
const TELEGRAM_CALLBACK_DATA_LIMIT_BYTES = 64;
const telegramCallbackBytes = (actionId: string, value: string) =>
  Buffer.byteLength(`chat:${JSON.stringify({ a: actionId, v: value })}`, "utf8");

const payload = {
  tenantId: TENANT,
  excursionId: EXCURSION,
  ruleName: "Urgent low",
  subjectName: "Alex",
  glucoseValue: 54,
  trend: "SingleDown",
  trendRate: -1.4,
  readingTimestamp: "2026-01-01T00:00:00.000Z",
} as AlertPayload;

const buttons = () => cardButtons(AlertCard({ payload }));

const ago = (minutes: number) =>
  new Intl.RelativeTimeFormat(new Intl.DateTimeFormat().resolvedOptions().locale, {
    style: "narrow",
    numeric: "auto",
  }).format(-minutes, "minute");

describe("AlertCard button values", () => {
  it("puts every button inside Telegram's callback_data budget", () => {
    expect(buttons()).not.toHaveLength(0);
    for (const { id, value } of buttons()) {
      expect(telegramCallbackBytes(id, value ?? "")).toBeLessThanOrEqual(
        TELEGRAM_CALLBACK_DATA_LIMIT_BYTES,
      );
    }
  });

  it("addresses every button at the tenant and the excursion", () => {
    for (const { value } of buttons()) {
      expect(decodeActionValue(value)).toEqual({
        tenantKey: encodeTenantKey(TENANT),
        excursionId: EXCURSION,
        unreadableExcursion: false,
      });
    }
  });

  it("offers only the action the bot can carry out", () => {
    expect(buttons().map((b) => b.id)).toEqual(["ack_alert"]);
  });
});

describe("AlertCard for a rule that cannot run", () => {
  const notice = {
    ...payload,
    kind: "unevaluable",
    glucoseValue: null,
    trend: null,
    trendRate: null,
    severity: "critical",
  } as AlertPayload;

  it("names the rule, says it cannot run, and shows no reading or action", () => {
    const card = AlertCard({ payload: notice });

    expect(cardTitle(card)).toBe("CRITICAL: Urgent low");
    expect(cardTexts(card)).toEqual([
      "This rule cannot run as set, so it sends no alerts. Edit its conditions and save it.",
    ]);
    expect(cardFields(card)).toHaveLength(0);
    expect(cardButtons(card)).toHaveLength(0);
  });

  it("renders the notice for a rule whose condition type the engine does not know", () => {
    const card = AlertCard({ payload: { ...notice, alertType: null } });

    expect(cardTitle(card)).toBe("CRITICAL: Urgent low");
    expect(cardButtons(card)).toHaveLength(0);
  });
});

describe("AlertCard for a rule held for 24 hours", () => {
  const held = {
    ...payload,
    kind: "held",
    glucoseValue: null,
    trend: null,
    trendRate: null,
    severity: "warning",
    heldFacts: [
      { fact: "glucose", reason: "stale" },
      { fact: "pump", reason: "absent" },
    ],
  } as AlertPayload;

  it("names the rule, says it waits for data, lists each fact, and offers no action", () => {
    const card = AlertCard({ payload: held });

    expect(cardTitle(card)).toBe("Warning: Urgent low");
    expect(cardTexts(card)).toEqual([
      "This rule has waited 24 hours for usable data, so it cannot send alerts until usable data comes back.",
    ]);
    expect(cardFields(card)).toEqual(["Glucose readings: Too old", "Pump data: Missing"]);
    expect(cardButtons(card)).toHaveLength(0);
  });

  it("says the alert is closed when the hold closed one", () => {
    const card = AlertCard({ payload: { ...held, kind: "held_closed" } });

    expect(cardTexts(card)).toEqual([
      "This alert had no usable data for 24 hours, so Nocturne closes the alert. The rule cannot send alerts until usable data comes back.",
    ]);
    expect(cardButtons(card)).toHaveLength(0);
  });

  it("shows a fact or reason this build does not know as it was sent", () => {
    const card = AlertCard({ payload: { ...held, heldFacts: [{ fact: "warp_core", reason: "melted" }] } });

    expect(cardFields(card)).toEqual(["warp_core: melted"]);
  });
});

describe("AlertCard with no glucose reading", () => {
  const signalLoss = {
    ...payload,
    alertType: "signal_loss",
    ruleName: "Signal loss",
    glucoseValue: null,
    trend: null,
    trendRate: null,
  } as AlertPayload;

  it("says the same for a staleness alert", () => {
    const card = AlertCard({ payload: { ...signalLoss, alertType: "staleness" } });

    expect(cardTexts(card)).toContain("There is no glucose reading for Alex.");
  });

  it("does not say there is no reading for another kind that has no value", () => {
    const card = AlertCard({
      payload: { ...signalLoss, alertType: "threshold", subjectName: "Test fire" },
    });

    expect(cardTexts(card).join(" ")).not.toContain("no glucose reading");
    expect(cardTexts(card)).toContain("Test fire is N/A ");
    expect(cardFields(card).some((f) => f.startsWith("Time: "))).toBe(true);
  });

  it("says there is no reading and shows no reading time, and can still be acknowledged", () => {
    const card = AlertCard({ payload: signalLoss });

    expect(cardTexts(card)).toContain("There is no glucose reading for Alex.");
    expect(cardTexts(card).join(" ")).not.toMatch(/N\/A|Alex is/);
    expect(cardFields(card)).toHaveLength(0);
    expect(cardButtons(card).map((b) => b.id)).toEqual(["ack_alert"]);
  });
});

describe("AcknowledgedCard", () => {
  it("states the outcome and offers no action", () => {
    const card = AcknowledgedCard({ detail: "By Sam Tester." });

    expect(cardButtons(card)).toHaveLength(0);
    expect(cardTitle(card)).toBe("Alert acknowledged");
    expect(cardTexts(card)).toContain("By Sam Tester.");
  });
});

describe("ActiveAlertsCard", () => {
  const excursion = (
    id: string,
    ruleName: string,
    over: Partial<ActiveExcursionResponse> = {},
  ): ActiveExcursionResponse => ({
    id,
    ruleName,
    startedAt: new Date(Date.now() - 12 * 60_000).toISOString(),
    ...over,
  });

  it("renders one row per active excursion, acknowledged ones marked", () => {
    const card = ActiveAlertsCard({
      excursions: [
        excursion("e1", "Urgent low"),
        excursion("e2", "High", { acknowledgedAt: new Date().toISOString() }),
      ],
    });

    expect(cardTitle(card)).toBe("Active alerts");
    expect(cardFields(card)).toEqual([
      `Urgent low: Firing, started ${ago(12)}`,
      `High: Acknowledged, started ${ago(12)}`,
    ]);
  });

  it("marks a snoozed excursion with the time the server resumes it", () => {
    const until = new Date(Date.now() + 20 * 60_000);
    const card = ActiveAlertsCard({
      excursions: [excursion("e1", "Low", { snoozedUntil: until.toISOString() })],
    });

    expect(cardFields(card)).toEqual([
      `Low: Snoozed until ${until.toLocaleTimeString()}, started ${ago(12)}`,
    ]);
  });

  it("reads an acknowledged excursion as acknowledged even while snoozed", () => {
    const card = ActiveAlertsCard({
      excursions: [
        excursion("e1", "Low", {
          acknowledgedAt: new Date().toISOString(),
          snoozedUntil: new Date(Date.now() + 60_000).toISOString(),
        }),
      ],
    });

    expect(cardFields(card)).toEqual([`Low: Acknowledged, started ${ago(12)}`]);
  });

  it("says so rather than printing a bogus age when the start time is missing", () => {
    const card = ActiveAlertsCard({
      excursions: [excursion("e1", "Urgent low", { startedAt: undefined })],
    });

    expect(cardFields(card)).toEqual([
      "Urgent low: Firing, started at an unknown time",
    ]);
  });
});

describe("AlertCard severity", () => {
  const card = (severity: unknown) =>
    AlertCard({ payload: { ...payload, severity } as unknown as AlertPayload });

  const rendering = (severity: unknown) =>
    JSON.stringify({
      title: cardTitle(card(severity)),
      texts: cardTexts(card(severity)),
      fields: cardFields(card(severity)),
      styles: cardStyles(card(severity)),
    });

  it("tells a critical alert apart from a warning", () => {
    expect(rendering("critical")).not.toBe(rendering("warning"));
  });

  it("marks every severity the API sends, each one distinctly", () => {
    expect(cardTitle(card("critical"))).toBe("CRITICAL: Urgent low");
    expect(cardTitle(card("warning"))).toBe("Warning: Urgent low");
    expect(cardTitle(card("info"))).toBe("Info: Urgent low");
  });

  it("emphasises the reading only when the alert is critical", () => {
    expect(cardStyles(card("critical"))).toContain("bold");
    for (const severity of ["warning", "info", undefined, "meltdown"]) {
      expect(cardStyles(card(severity))).not.toContain("bold");
    }
  });

  it("falls back to a plain alert when the severity is missing or unrecognised", () => {
    for (const severity of [undefined, null, "", "meltdown", 7, {}]) {
      expect(cardTitle(card(severity))).toBe("Alert: Urgent low");
      expect(cardButtons(card(severity))).toHaveLength(1);
    }
  });
});
