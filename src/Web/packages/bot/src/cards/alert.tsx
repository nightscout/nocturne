import { Card, CardText, Fields, Field, Actions, Button } from "chat";
import type { AlertPayload } from "../types.js";
import { formatGlucose, trendArrow } from "../lib/format.js";
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
        <Button id="mute_30" value={target}>
          Mute 30 min
        </Button>
      </Actions>
    </Card>
  );
}

export function AcknowledgedCard(props: {
  originalTitle: string;
  acknowledgedBy: string;
}) {
  return (
    <Card title={`${props.originalTitle} [Acknowledged]`}>
      <CardText>{`Acknowledged by ${props.acknowledgedBy}`}</CardText>
    </Card>
  );
}

export function ResolvedCard(props: {
  originalTitle: string;
  resolvedValue?: number;
  unit?: "mg/dL" | "mmol/L";
}) {
  const { unit = "mg/dL" } = props;
  const valueStr =
    props.resolvedValue != null
      ? formatGlucose(props.resolvedValue, unit)
      : "";
  return (
    <Card title={`${props.originalTitle} [Resolved]`}>
      <CardText>{`Resolved${valueStr ? ` -- back in range (${valueStr})` : ""}`}</CardText>
    </Card>
  );
}
