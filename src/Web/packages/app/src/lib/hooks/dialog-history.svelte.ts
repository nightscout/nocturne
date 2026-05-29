import { pushState } from "$app/navigation";
import { page } from "$app/state";

/**
 * Syncs a dialog's open state with browser history so the back button (or the
 * Android/mobile back gesture) closes the dialog instead of navigating away
 * from the page.
 *
 * When the dialog opens, a shallow-routing history entry is pushed via
 * SvelteKit's `pushState`. Popping that entry — by pressing back — clears the
 * flag from `page.state` and runs the dialog's `close` callback. Closing the
 * dialog any other way (Save, Cancel, Escape, overlay click) pops our entry
 * with `history.back()` so the history stack stays balanced.
 *
 * Call once during component init, e.g. inside a dialog component that owns the
 * `open`/`onClose` contract:
 *
 *   useDialogHistory(() => open, onClose);
 *
 * @param isOpen Reactive getter for the dialog's open state.
 * @param close  Closes the dialog (and runs any associated cleanup). Invoked
 *               when the user pops our history entry via the back button.
 */
export function useDialogHistory(isOpen: () => boolean, close: () => void) {
  // Unique per instance so multiple dialogs on one page never collide, and so a
  // stale flag left in history after a reload can't be mistaken for ours.
  const key = `dialog:${crypto.randomUUID()}`;

  // Whether this dialog currently owns the topmost pushed history entry.
  // Plain (non-reactive) bookkeeping — the effects react to `isOpen` and
  // `page.state`, not to this flag.
  let owns = false;

  // Push our history entry when the dialog opens; pop it when the dialog is
  // closed programmatically while our entry is still current.
  $effect(() => {
    const open = isOpen();
    const flagged = page.state[key] === true;

    if (open && !owns) {
      owns = true;
      pushState("", { ...page.state, [key]: true });
    } else if (!open && owns && flagged) {
      owns = false;
      history.back();
    }
  });

  // Close the dialog when the user pops our entry (back button / gesture):
  // SvelteKit reverts `page.state`, dropping our flag.
  $effect(() => {
    const flagged = page.state[key] === true;
    if (!flagged && owns) {
      owns = false;
      if (isOpen()) close();
    }
  });
}
