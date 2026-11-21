import type { AlertRuleResponse } from "@nocturne/api-client";

export const lastLowCoverMessage =
  "This is the only rule that alerts for low glucose. No alert for low glucose will remain.";

/** Whether turning the rule off, or deleting it, leaves the tenant with no low alert. */
export function endsLowCover(
  rule: Pick<AlertRuleResponse, "isLastLowCover"> | undefined,
): boolean {
  return rule?.isLastLowCover === true;
}
