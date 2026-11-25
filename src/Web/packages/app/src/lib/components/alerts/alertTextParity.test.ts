import { describe, expect, it } from "vitest";
import { AlertLimitFact, AlertRuleSeverity, ForecastCurve } from "@nocturne/api-client";
import {
  CARD_TEXT,
  CURVE_NAMES,
  FACT_NAMES,
  LIMIT_FACT_NAMES,
  REASONS,
  SEVERITY_TITLES,
} from "@nocturne/bot/alert-text";
import { resolveNotificationLabel } from "#lib/utils/notification-labels.js";
import { forecastCurveLabel, limitFactWords } from "./alert-labels.svelte";
import { KNOWN_FACTS, KNOWN_REASONS, heldFactName, heldReason } from "./holdText.svelte";
import { severityLabel } from "./severityLabel.svelte";

/** The only ways the bot's words may differ from the app's. */
const ALLOWED_DIFFERENCES = {
  /** A bot field value starts with a capital; the app's reason continues a sentence. */
  reasonFirstLetter: (appReason: string) => appReason.charAt(0).toUpperCase() + appReason.slice(1),
  /** The bot shouts these severities in a card title. */
  shoutedSeverities: new Set<AlertRuleSeverity>([AlertRuleSeverity.Critical]),
  /** The app's held subtitle points at the rule's page; a chat card has no rule to open. */
  heldSubtitleSuffix: " Open the rule to see the cause.",
};

const sorted = (keys: Iterable<string>) => [...keys].sort();

describe("the app's and the bot's alert text", () => {
  it("names the same held facts", () => {
    expect(sorted(Object.keys(FACT_NAMES))).toEqual(sorted(KNOWN_FACTS));
    for (const fact of KNOWN_FACTS) {
      expect(FACT_NAMES[fact], fact).toBe(heldFactName(fact));
    }
  });

  it("names the same held reasons", () => {
    expect(sorted(Object.keys(REASONS))).toEqual(sorted(KNOWN_REASONS));
    for (const reason of KNOWN_REASONS) {
      expect(REASONS[reason], reason).toBe(ALLOWED_DIFFERENCES.reasonFirstLetter(heldReason(reason)));
    }
  });

  it("titles the same severities", () => {
    const severities = Object.values<AlertRuleSeverity>(AlertRuleSeverity);
    expect(sorted(Object.keys(SEVERITY_TITLES))).toEqual(sorted(severities));
    for (const severity of severities) {
      const label = severityLabel(severity);
      const expected = ALLOWED_DIFFERENCES.shoutedSeverities.has(severity) ? label.toUpperCase() : label;
      expect(SEVERITY_TITLES[severity], severity).toBe(expected);
    }
  });

  it("names the same forecast curves", () => {
    const curves = Object.values<ForecastCurve>(ForecastCurve);
    expect(sorted(Object.keys(CURVE_NAMES))).toEqual(sorted(curves));
    for (const curve of curves) {
      expect(CURVE_NAMES[curve], curve).toBe(forecastCurveLabel(curve));
    }
  });

  it("names the same limit facts", () => {
    const facts = Object.values<AlertLimitFact>(AlertLimitFact);
    expect(sorted(Object.keys(LIMIT_FACT_NAMES))).toEqual(sorted(facts));
    for (const fact of facts) {
      expect(LIMIT_FACT_NAMES[fact], fact).toBe(limitFactWords(fact));
    }
  });

  it("gives the same text for a rule that cannot run and a rule that is held", () => {
    expect(CARD_TEXT.unevaluable).toBe(resolveNotificationLabel("alert_unevaluable_subtitle"));
    expect(CARD_TEXT.held + ALLOWED_DIFFERENCES.heldSubtitleSuffix).toBe(
      resolveNotificationLabel("alert_held_subtitle"),
    );
    expect(CARD_TEXT.heldClosed).toBe(resolveNotificationLabel("alert_held_closed_subtitle"));
  });
});
