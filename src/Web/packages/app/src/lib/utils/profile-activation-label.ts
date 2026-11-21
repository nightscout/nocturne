import type { ProfileActivationDto } from "@nocturne/api-client";
import { getDataSourceDisplayName } from "#lib/utils/data-source-display.js";

/** The app or connector that wrote an activation, by name; undefined when it carries neither. */
export function profileActivationSource(
  activation: ProfileActivationDto | null | undefined
): string | undefined {
  return activation?.app ?? getDataSourceDisplayName(activation?.dataSource) ?? undefined;
}
