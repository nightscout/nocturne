import { page } from "$app/state";
import { satisfiesScope } from "#lib/authorization/scopes.js";
import type { EntryCategoryId } from "#lib/constants/entry-categories.js";

/**
 * The scope each kind of entry is written under: the `WriteScope` of its V4 controller. BG checks
 * follow glucose and device events follow devices, not the treatments category their legacy
 * event types came from.
 */
const ENTRY_WRITE_SCOPES: Record<EntryCategoryId, string> = {
  bolus: "treatments.readwrite",
  carbs: "treatments.readwrite",
  note: "treatments.readwrite",
  basalInjection: "treatments.readwrite",
  bgCheck: "glucose.readwrite",
  deviceEvent: "devices.readwrite",
};

/** Whether the viewer may write entries of `kind`, so the action that adds one is offered. */
export function canWriteEntryKind(kind: EntryCategoryId): boolean {
  return satisfiesScope(page.data.effectivePermissions ?? [], ENTRY_WRITE_SCOPES[kind]);
}

/**
 * Whether the viewer may record entries, so the actions that start or edit one are offered. A new
 * entry opens as a bolus with carbs, so this is the treatments scope; a share link and a
 * read-only member hold none.
 */
export function canLogEvents(): boolean {
  return canWriteEntryKind("bolus");
}
