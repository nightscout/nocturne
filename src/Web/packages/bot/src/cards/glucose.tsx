import { Card, Fields, Field } from "chat";
import type { SensorGlucoseReading } from "../types.js";
import { formatGlucose, timeAgo, trendArrow } from "../lib/format.js";

export function GlucoseCard(props: {
  reading: SensorGlucoseReading;
  unit?: "mg/dL" | "mmol/L";
}) {
  const { reading, unit = "mg/dL" } = props;
  const value = reading.mgdl != null ? formatGlucose(reading.mgdl, unit) : "N/A";
  const arrow = reading.direction ? trendArrow(reading.direction) : "";

  return (
    <Card title="Glucose Reading">
      <Fields>
        <Field label="BG" value={`${value} ${arrow}`} />
        <Field label="Updated" value={reading.mills != null ? timeAgo(reading.mills) : "N/A"} />
      </Fields>
    </Card>
  );
}
