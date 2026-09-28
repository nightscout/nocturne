import { Card, CardText, Fields, Field, Actions, Button } from "chat";
import type { ActiveExcursion, AlertPayload, AlertSeverity } from "../types.js";
import { formatGlucose, timeAgo, trendArrow } from "../lib/format.js";
import { encodeActionValue } from "../lib/action-value.js";
import { isKnownSeverity } from "../lib/severity.js";

/**
 * Card titles per severity. Every platform gets this, including the ones with
 * no colour to carry (Telegram, WhatsApp, e-mail); on Discord and Slack it sits
 * alongside the coloured bar that `../adapters/accented-discord.ts` applies.
 *
 * Labels mirror `severity.ts` in `@nocturne/app`, and an unrecognised value
 * degrades to a neutral title as the app degrades to a muted style.
 */
const SEVERITY_TITLES: Record<AlertSeverity, string> = {
  critical: "CRITICAL",
  warning: "Warning",
  info: "Info",
};

export function AlertCard(props: {
  payload: AlertPayload;
  unit?: "mg/dL" | "mmol/L";
}) {
  const { payload, unit = "mg/dL" } = props;
  const value =
    payload.glucoseValue != null
      ? formatGlucose(payload.glucoseValue, unit)
      : "N/A";
  const arrow = payload.trend ? trendArrow(payload.trend) : "";
  const target = encodeActionValue({
    tenantId: payload.tenantId,
    excursionId: payload.excursionId,
  });

  const severity = isKnownSeverity(payload.severity) ? payload.severity : undefined;
  const titlePrefix = severity ? SEVERITY_TITLES[severity] : "Alert";

  return (
    <Card title={`${titlePrefix}: ${payload.ruleName}`}>
      <CardText style={severity === "critical" ? "bold" : "plain"}>
        {`${payload.subjectName} is ${value} ${arrow}`}
      </CardText>
      <Fields>
        <Field
          label="Time"
          value={new Date(payload.readingTimestamp).toLocaleTimeString()}
        />
        {payload.trendRate != null && (
          <Field
            label="Rate"
            value={`${payload.trendRate > 0 ? "+" : ""}${payload.trendRate.toFixed(1)}/min`}
          />
        )}
      </Fields>
      <Actions>
        <Button id="ack_alert" value={target} style="primary">
          Acknowledge
        </Button>
      </Actions>
    </Card>
  );
}

/**
 * Posted in the thread once Acknowledge is tapped. The alert card is left
 * standing rather than edited: an `ActionEvent` carries the message id but
 * none of the card's content, so an edit would have to replace the reading,
 * trend, subject and timestamp with this summary.
 */
export function AcknowledgedCard(props: { detail: string }) {
  return (
    <Card title="Alert acknowledged">
      <CardText>{props.detail}</CardText>
    </Card>
  );
}

function status(excursion: ActiveExcursion): string {
  if (excursion.acknowledgedAt) return "Acknowledged";
  if (excursion.snoozedUntil) {
    return `Snoozed until ${new Date(excursion.snoozedUntil).toLocaleTimeString()}`;
  }
  return "Firing";
}

export function ActiveAlertsCard(props: { excursions: ActiveExcursion[] }) {
  return (
    <Card title="Active alerts">
      <Fields>
        {props.excursions.map((excursion) => (
          <Field
            key={excursion.id}
            label={excursion.ruleName ?? "Alert"}
            value={`${status(excursion)}, started ${
              excursion.startedAt
                ? timeAgo(new Date(excursion.startedAt).getTime())
                : "at an unknown time"
            }`}
          />
        ))}
      </Fields>
    </Card>
  );
}
