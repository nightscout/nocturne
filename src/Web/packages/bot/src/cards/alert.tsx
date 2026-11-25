import { Card, CardText, Fields, Field, Actions, Button } from "chat";
import { AlertConditionType, AlertPayloadKind, AlertRuleSeverity, type ActiveExcursionResponse } from "@nocturne/api-client";
import type { AlertPayload } from "../types.js";
import { formatGlucose, timeAgo, trendArrow } from "../lib/format.js";
import { encodeActionValue } from "../lib/action-value.js";
import { SEVERITY_TITLES, isKnownSeverity } from "../lib/severity.js";
import { CARD_TEXT } from "../lib/alert-card-text.js";
import { describeHeldFacts } from "../lib/held-facts.js";
import { describeMetBy } from "../lib/met-by.js";

/**
 * The condition kinds that fire on readings stopping, so a payload of one with no glucose value
 * is an alert about having no reading rather than a reading the card failed to get.
 */
const NO_READING_KINDS: ReadonlySet<string> = new Set<AlertConditionType>([
  AlertConditionType.SignalLoss,
  AlertConditionType.Staleness,
]);

export function AlertCard(props: {
  payload: AlertPayload;
  unit?: "mg/dL" | "mmol/L";
}) {
  const { payload, unit = "mg/dL" } = props;
  if (payload.kind === AlertPayloadKind.Unevaluable) return UnevaluableCard({ payload });
  if (payload.kind === AlertPayloadKind.Held || payload.kind === AlertPayloadKind.HeldClosed) {
    return HeldCard({ payload, closed: payload.kind === AlertPayloadKind.HeldClosed });
  }
  const noReading = payload.glucoseValue == null && payload.alertType != null && NO_READING_KINDS.has(payload.alertType);
  const value =
    payload.glucoseValue != null
      ? formatGlucose(payload.glucoseValue, unit)
      : "N/A";
  const arrow = payload.trend ? trendArrow(payload.trend) : "";
  const text = noReading
    ? `There is no glucose reading for ${payload.subjectName}.`
    : `${payload.subjectName} is ${value} ${arrow}`;
  const target = encodeActionValue({
    tenantId: payload.tenantId,
    excursionId: payload.excursionId,
  });

  const severity = isKnownSeverity(payload.severity) ? payload.severity : undefined;
  const titlePrefix = severity ? SEVERITY_TITLES[severity] : "Alert";

  return (
    <Card title={`${titlePrefix}: ${payload.ruleName}`}>
      <CardText style={severity === AlertRuleSeverity.Critical ? "bold" : "plain"}>
        {text}
      </CardText>
      <Fields>
        {!noReading && (
          <Field
            label="Time"
            value={new Date(payload.readingTimestamp).toLocaleTimeString()}
          />
        )}
        {payload.trendRate != null && (
          <Field
            label="Rate"
            value={`${payload.trendRate > 0 ? "+" : ""}${payload.trendRate.toFixed(1)}/min`}
          />
        )}
        {describeMetBy(payload.metBy, unit).map((met) => (
          <Field key={met.key} label={met.label} value={met.value} />
        ))}
      </Fields>
      <Actions>
        <Button id="ack_alert" value={target} style="primary">
          Acknowledge
        </Button>
      </Actions>
    </Card>
  );
}

function UnevaluableCard(props: { payload: AlertPayload }) {
  const { payload } = props;
  const severity = isKnownSeverity(payload.severity) ? payload.severity : undefined;
  const titlePrefix = severity ? SEVERITY_TITLES[severity] : "Alert";

  return (
    <Card title={`${titlePrefix}: ${payload.ruleName}`}>
      <CardText>{CARD_TEXT.unevaluable}</CardText>
    </Card>
  );
}

function HeldCard(props: { payload: AlertPayload; closed: boolean }) {
  const { payload, closed } = props;
  const severity = isKnownSeverity(payload.severity) ? payload.severity : undefined;
  const titlePrefix = severity ? SEVERITY_TITLES[severity] : "Alert";
  const text = closed ? CARD_TEXT.heldClosed : CARD_TEXT.held;

  return (
    <Card title={`${titlePrefix}: ${payload.ruleName}`}>
      <CardText>{text}</CardText>
      <Fields>
        {describeHeldFacts(payload.heldFacts).map((fact) => (
          <Field key={fact.label} label={fact.label} value={fact.value} />
        ))}
      </Fields>
    </Card>
  );
}

/**
 * Posted in the thread once Acknowledge is tapped. The alert card is left
 * standing rather than edited: an `ActionEvent` carries the message id but
 * none of the card's content, so an edit would have to replace the reading,
 * trend, subject and timestamp with this summary.
 */
export function AcknowledgedCard(props: { title?: string; detail: string }) {
  return (
    <Card title={props.title ?? "Alert acknowledged"}>
      <CardText>{props.detail}</CardText>
    </Card>
  );
}

function status(excursion: ActiveExcursionResponse): string {
  if (excursion.acknowledgedAt) return "Acknowledged";
  if (excursion.snoozedUntil) {
    return `Snoozed until ${new Date(excursion.snoozedUntil).toLocaleTimeString()}`;
  }
  return "Firing";
}

export function ActiveAlertsCard(props: { excursions: ActiveExcursionResponse[] }) {
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
