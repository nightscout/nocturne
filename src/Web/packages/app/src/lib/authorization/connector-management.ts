import { satisfiesScope } from "./scopes";

/**
 * Whether the viewer may change connectors and data sources: configure, enable,
 * sync, reset or delete them. The endpoints require `tenant.settings` and also
 * refuse the demo tenant's shared visitor account, which holds that scope, so
 * the API reports that refusal (`refusedAsDemoSubject`) alongside the scopes.
 */
export function canManageConnectors(
  granted: readonly string[] | undefined,
  refusedAsDemoSubject: boolean | undefined
): boolean {
  return (
    refusedAsDemoSubject !== true &&
    satisfiesScope(granted ?? [], "tenant.settings")
  );
}
