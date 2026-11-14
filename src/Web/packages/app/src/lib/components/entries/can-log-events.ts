import { page } from "$app/state";
import { satisfiesScope } from "#lib/authorization/scopes.js";

/**
 * Whether the viewer may record entries, so the actions that start one are offered. A share link
 * and a read-only member hold no `treatments.readwrite`.
 */
export function canLogEvents(): boolean {
  return satisfiesScope(page.data.effectivePermissions ?? [], "treatments.readwrite");
}
