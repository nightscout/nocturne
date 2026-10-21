import type { ProfileActivationDto } from "$api-clients";

/** "Rhys", "Rhys 110%", "Rhys 110% +1h": the profile with the percentage and shift it runs at. */
export function profileActivationLabel(
  activation: ProfileActivationDto | null | undefined
): string | undefined {
  if (!activation?.baseProfileName) return undefined;
  const parts = [activation.baseProfileName];
  if (activation.percentage != null && activation.percentage !== 100)
    parts.push(`${activation.percentage}%`);
  if (activation.timeshiftHours)
    parts.push(`${activation.timeshiftHours > 0 ? "+" : ""}${activation.timeshiftHours}h`);
  return parts.join(" ");
}
