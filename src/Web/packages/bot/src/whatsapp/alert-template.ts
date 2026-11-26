import type { WhatsAppTemplateMessage } from "@chat-adapter/whatsapp";
import { AlertConditionType, AlertPayloadKind } from "@nocturne/api-client";
import type { AlertPayload } from "../types.js";
import { encodeActionValue } from "../lib/action-value.js";
import { formatGlucose, trendArrow } from "../lib/format.js";

/** The approved WhatsApp template an alert is sent as outside the customer service window. */
export interface AlertTemplate {
  name: string;
  /** Meta's language code the template was approved in, such as `en` or `en_US`. */
  language: string;
}

/** The action the template's quick reply runs; the alert card's Acknowledge button runs the same. */
const ACKNOWLEDGE_ACTION = "ack_alert";

/**
 * Marks a template quick-reply payload as one the bot wrote. Meta returns the payload as written,
 * and the adapter reads it back into the action and value a card button carries.
 */
const PAYLOAD_PREFIX = "nocturne|";

const NO_READING_KINDS: ReadonlySet<string> = new Set<AlertConditionType>([
  AlertConditionType.SignalLoss,
  AlertConditionType.Staleness,
]);

/** Keeps the three parameters well inside Meta's limit of 1,024 characters for the whole body. */
const MAX_PARAMETER_LENGTH = 100;

/**
 * Meta refuses a template parameter with a line break, a tab or more than four spaces in a row,
 * and a body over its length limit.
 */
function parameter(text: string): string {
  const clean = text.replace(/[\r\n\t]+/g, " ").replace(/ {5,}/g, "    ").trim() || "-";
  return clean.length > MAX_PARAMETER_LENGTH ? `${clean.slice(0, MAX_PARAMETER_LENGTH - 1).trimEnd()}…` : clean;
}

/** The reading as the alert card shows it: value, unit and trend arrow. */
function reading(payload: AlertPayload): string {
  if (payload.glucoseValue == null) {
    return payload.alertType != null && NO_READING_KINDS.has(payload.alertType) ? "no reading" : "unknown";
  }
  const arrow = payload.trend ? ` ${trendArrow(payload.trend)}` : "";
  return `${formatGlucose(payload.glucoseValue, "mg/dL")}${arrow}`;
}

/**
 * The alert as the configured template: body {{1}} the person, {{2}} the alert's name, {{3}} the
 * reading with its unit and trend, and a quick reply that acknowledges the alert. Null for a
 * notice that a rule cannot run or is on hold: the template states a reading and offers to
 * acknowledge, which such a notice has neither of.
 */
export function alertTemplateMessage(template: AlertTemplate, payload: AlertPayload): WhatsAppTemplateMessage | null {
  if (payload.kind != null && payload.kind !== AlertPayloadKind.Excursion) return null;
  return {
    name: template.name,
    language: template.language,
    components: [
      {
        type: "body",
        parameters: [payload.subjectName, payload.ruleName, reading(payload)].map((text) => ({
          type: "text" as const,
          text: parameter(text),
        })),
      },
      {
        type: "button",
        sub_type: "quick_reply",
        index: 0,
        parameters: [
          {
            type: "payload",
            payload: templatePayload(
              ACKNOWLEDGE_ACTION,
              encodeActionValue({ tenantId: payload.tenantId, excursionId: payload.excursionId }),
            ),
          },
        ],
      },
    ],
  };
}

export function templatePayload(actionId: string, value: string): string {
  return `${PAYLOAD_PREFIX}${actionId}|${value}`;
}

/**
 * The action and value a quick-reply payload the bot wrote carries, or null for any other payload.
 * Only the action a template offers is read back.
 */
export function readTemplatePayload(payload: string): { actionId: string; value: string } | null {
  if (!payload.startsWith(PAYLOAD_PREFIX)) return null;
  const rest = payload.slice(PAYLOAD_PREFIX.length);
  const split = rest.indexOf("|");
  if (split <= 0 || rest.slice(0, split) !== ACKNOWLEDGE_ACTION) return null;
  return { actionId: ACKNOWLEDGE_ACTION, value: rest.slice(split + 1) };
}
