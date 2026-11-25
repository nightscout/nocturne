import { AsyncLocalStorage } from "node:async_hooks";
import { statusHex, statusRgb, type StatusToken } from "@nocturne/ui/tokens";
import { AlertRuleSeverity } from "@nocturne/api-client";
import { createLogger } from "./logger.js";

const logger = createLogger();

export const isKnownSeverity = (value: unknown): value is AlertRuleSeverity =>
  Object.values<unknown>(AlertRuleSeverity).includes(value);

/**
 * The design-system token each severity is coloured from. The annotation is the
 * drift guard: a severity with no token of that name fails to compile, and the
 * value itself lives once, in `@nocturne/ui/tokens`.
 */
const TOKEN_BY_SEVERITY: Record<AlertRuleSeverity, StatusToken> = {
  [AlertRuleSeverity.Critical]: "critical",
  [AlertRuleSeverity.Warning]: "warning",
  [AlertRuleSeverity.Info]: "info",
};

export interface AlertAccent {
  /** `#rrggbb`, for a Slack attachment. */
  hex: string;
  /** The same colour as the 24-bit integer a Discord embed takes. */
  rgb: number;
}

function accentFor(severity: unknown): AlertAccent | undefined {
  if (!isKnownSeverity(severity)) {
    return undefined;
  }
  const token = TOKEN_BY_SEVERITY[severity];
  return { hex: statusHex(token), rgb: statusRgb(token) };
}

const scope = new AsyncLocalStorage<AlertAccent>();

/**
 * Carries an alert's colour to the adapter that posts it. The colour is a
 * property of the alert, but `chat`'s card API has nowhere to put it and its
 * adapters take no per-message colour, so it travels beside the call instead of
 * inside the card. Outside this scope — every other message the bot sends —
 * adapters behave exactly as they ship.
 *
 * A colour that will not resolve costs the alert its colour, never its
 * delivery: a carer has to receive the message even if it arrives grey.
 */
export function withAlertAccent<T>(severity: unknown, run: () => T): T {
  let accent: AlertAccent | undefined;
  try {
    accent = accentFor(severity);
  } catch (err) {
    logger.error("Could not resolve the accent colour for this alert:", err);
  }
  return accent ? scope.run(accent, run) : run();
}

export function currentAlertAccent(): AlertAccent | undefined {
  return scope.getStore();
}

/**
 * Card titles per severity. Every platform gets this, including the ones with
 * no colour to carry (Telegram, WhatsApp, e-mail); on Discord and Slack it sits
 * alongside the coloured bar that `../adapters/accented-discord.ts` applies.
 */
export const SEVERITY_TITLES: Record<AlertRuleSeverity, string> = {
  [AlertRuleSeverity.Critical]: "CRITICAL",
  [AlertRuleSeverity.Warning]: "Warning",
  [AlertRuleSeverity.Info]: "Info",
};
