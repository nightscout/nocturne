/**
 * Undo for a removal in the workspace: a toast that says what was removed, with an Undo action that
 * puts it back, or says why it cannot.
 */
import { toast } from "svelte-sonner";
import type { Reason, Workspace, WorkspaceState } from "./workspace.svelte.js";

/** The undo toasts on screen, which `dismissUndos` closes. */
let offered: (string | number)[] = [];

/** Says `message`, with an Undo action that runs `undo`; a reason `undo` gives is said as an error. */
export function offerUndo(message: string, undo: () => Reason): void {
  const id = toast(message, {
    // Longer than a toast that only informs: the member has to reach the action.
    duration: 10_000,
    onDismiss: () => forget(id),
    onAutoClose: () => forget(id),
    action: {
      label: "Undo",
      onClick: () => {
        forget(id);
        const reason = undo();
        if (reason) toast.error(reason);
      },
    },
  });
  offered.push(id);
}

function forget(id: string | number): void {
  offered = offered.filter((o) => o !== id);
}

/**
 * Closes every undo toast: called when the workspace they act on goes away, so an Undo never
 * changes a report that is no longer on screen.
 */
export function dismissUndos(): void {
  for (const id of offered) toast.dismiss(id);
  offered = [];
}

/**
 * Removes a signal or line from a chart through `remove`, then offers to undo it. Undo puts the chart's
 * state back as it was when nothing has changed on the chart since; after another change it adds the
 * removed part again through `readd`, which keeps that change and changes nothing when the part is
 * back already. False when `remove` refused.
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
