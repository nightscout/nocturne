import { AlertConditionType } from "$api-clients";
import { factLabel } from "./factText.svelte";

export function conditionLabel(
  conditionType: AlertConditionType | undefined
): string {
  switch (conditionType) {
    case undefined:
      return "Alert";
    case AlertConditionType.Composite:
      return "Combined conditions";
    case AlertConditionType.Not:
      return "Inverted condition";
    case AlertConditionType.Sustained:
      return "Sustained condition";
    default:
      return factLabel(conditionType);
  }
}
