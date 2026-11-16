/**
 * What this browser keeps for a signed-in member that holds their health data, removed when their
 * session ends: at sign-out, when the session expires or is refused, and on the sign-in page, which
 * a session that ended while the browser was closed lands on.
 */
import { clearAllDrafts } from "#lib/components/reports/custom/workspace/report-draft.svelte.js";

export function clearMemberStorage(): void {
  clearAllDrafts();
}
