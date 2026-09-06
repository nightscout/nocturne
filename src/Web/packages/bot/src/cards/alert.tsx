import { Card, CardText, Fields, Field, Actions, Button } from "chat";
import type { ActiveExcursion, AlertPayload } from "../types.js";
import { formatGlucose, timeAgo, trendArrow } from "../lib/format.js";
import { encodeActionValue } from "../lib/action-value.js";

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

  return (
    <Card title={`Alert: ${payload.ruleName}`}>
      <CardText>{`${payload.subjectName} is ${value} ${arrow}`}</CardText>
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

export function ActiveAlertsCard(props: { excursions: ActiveExcursion[] }) {
  return (
    <Card title="Active alerts">
      <Fields>
        {props.excursions.map((excursion) => (
          <Field
            key={excursion.id}
            label={excursion.ruleName ?? "Alert"}
            value={`${excursion.acknowledgedAt ? "Acknowledged" : "Firing"}, started ${
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
