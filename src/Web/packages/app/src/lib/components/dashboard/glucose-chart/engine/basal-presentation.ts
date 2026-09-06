import { BasalDeliveryOrigin } from "$lib/api";

/**
 * Whether a basal point departs from the schedule, which the chart paints in the
 * temp-basal colour and labels "Auto Basal" / "Temp Basal".
 *
 * A null `scheduledRate` means the server had no therapy profile to resolve one
 * from. That is an absence of knowledge, not a deviation, so it must not read as
 * one — the tooltip would otherwise assert a departure from a schedule nobody
 * knows, alongside a blank "Scheduled" row.
 */
export function isBasalAdjusted(
  origin: BasalDeliveryOrigin | undefined,
  rate: number | undefined,
  scheduledRate: number | null | undefined
): boolean {
  const canBeAdjusted =
    origin === BasalDeliveryOrigin.Algorithm ||
    origin === BasalDeliveryOrigin.Manual;

  return canBeAdjusted && scheduledRate != null && rate !== scheduledRate;
}
