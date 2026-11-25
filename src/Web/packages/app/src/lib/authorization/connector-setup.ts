import { satisfiesScope } from "./scopes";

/**
 * Whether the viewer may set up the site's data sources: connect, change,
 * sync and remove a connector's configuration. The endpoints require
 * `connectors.readwrite`, which `tenant.settings` satisfies, and refuse the
 * demo tenant's shared visitor account (`refusedAsDemoSubject`).
 */
export function canSetUpConnectors(
  granted: readonly string[] | undefined,
  refusedAsDemoSubject: boolean | undefined
): boolean {
  return (
    refusedAsDemoSubject !== true &&
    satisfiesScope(granted ?? [], "connectors.readwrite")
  );
}
