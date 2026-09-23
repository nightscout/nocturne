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
import type { ActiveExcursion, AlertPayload } from "../types.js";

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
    over: Partial<ActiveExcursion> = {},
  ): ActiveExcursion => ({
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
      "Urgent low: Firing, started 12 min ago",
      "High: Acknowledged, started 12 min ago",
    ]);
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
