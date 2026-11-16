/**
 * Undo for a removal in the workspace: a toast that says what was removed, with an Undo action that
 * puts it back, or says why it cannot.
 */
import { toast } from "svelte-sonner";
import type { Reason, Workspace, WorkspaceState } from "./workspace.svelte.js";

/** Says `message`, with an Undo action that runs `undo`; a reason `undo` gives is said as an error. */
export function offerUndo(message: string, undo: () => Reason): void {
  toast(message, {
    // Longer than a toast that only informs: the member has to reach the action.
    duration: 10_000,
    action: {
      label: "Undo",
      onClick: () => {
        const reason = undo();
        if (reason) toast.error(reason);
      },
    },
  });
}

/**
 * Removes a signal or line from a chart through `remove`, then offers to undo it. Undo puts the chart's
 * state back as it was when nothing has changed on the chart since; after another change it adds the
 * removed part again through `readd`, which keeps that change. False when `remove` refused.
 */
export function removeWithUndo(ws: Workspace, message: string, remove: () => Reason, readd: () => Reason): boolean {
  const before: WorkspaceState = $state.snapshot(ws.state);
  if (remove()) return false;
  const after = JSON.stringify($state.snapshot(ws.state));
  offerUndo(message, () => {
    if (JSON.stringify($state.snapshot(ws.state)) !== after) return readd();
    ws.state = before;
    return null;
  });
  return true;
}
