import { pushState, replaceState } from "$app/navigation";
import { page } from "$app/state";

/**
 * Reflects an open dialog in a URL search param so it can be reloaded or
 * deep-linked. The owning page writes the param value (via `value`) when the
 * dialog opens and is responsible for reading the param on load and opening the
 * dialog itself — {@link useDialogHistory} only keeps the param in sync.
 */
export interface DialogHistoryParam {
  /** Search-param name that reflects the open dialog (e.g. "edit"). */
  name: string;
  /** Value written into the param when the dialog opens (e.g. "bolus:abc"). */
  value: () => string;
}

export interface DialogHistoryOptions {
  /**
   * Reflect the open state in a URL search param instead of an opaque
   * history-state flag, making the dialog reloadable / deep-linkable. Omit for
   * transient dialogs that only need back-button dismissal.
   */
  param?: DialogHistoryParam;
}

/**
 * Syncs a dialog's open state with browser history so the back button (or the
 * Android/mobile back gesture) closes the dialog instead of navigating away
 * from the page.
 *
 * When the dialog opens, a shallow-routing history entry is pushed via
 * SvelteKit's `pushState`. Popping that entry — by pressing back — runs the
 * dialog's `close` callback. Closing the dialog any other way (Save, Cancel,
 * Escape, overlay click) pops our entry with `history.back()` so the history
 * stack stays balanced.
 *
 * By default the pushed entry carries an opaque `page.state` flag (the URL is
 * unchanged). Pass {@link DialogHistoryOptions.param} to instead reflect the
 * open state in a URL search param so the dialog survives a reload and can be
 * deep-linked. When a dialog is opened from a param that is already present in
 * the URL (a deep link or reload), the hook adopts that state and clears it
 * with `replaceState` on close rather than calling `history.back()`, so closing
 * never navigates the user off the site.
 *
 * Call once during component init:
 *
 *   useDialogHistory(() => open, onClose);
 *   useDialogHistory(() => open, onClose, { param: { name: "edit", value } });
 *
 * @param isOpen Reactive getter for the dialog's open state.
 * @param close  Closes the dialog (and runs any associated cleanup). Invoked
 *               when the user pops our history entry via the back button.
 */
export function useDialogHistory(
  isOpen: () => boolean,
  close: () => void,
  options: DialogHistoryOptions = {},
) {
  const param = options.param;

  // Unique per instance so multiple opaque dialogs on one page never collide,
  // and so a stale flag left in history after a reload can't be mistaken for ours.
  const key = `dialog:${crypto.randomUUID()}`;

  // Bookkeeping (plain, non-reactive): the effects react to `isOpen`,
  // `page.state` and `page.url`, not to these flags.
  // - `pushed`: we added a poppable history entry (close via history.back()).
  // - `adopted`: the dialog opened from a param already in the URL on load
  //   (deep link / reload), so there's no entry of ours to pop — close by
  //   stripping the param with replaceState instead.
  let pushed = false;
  let adopted = false;

  // Whether the token that represents "this dialog is open" is currently present.
  const present = () =>
    param
      ? page.url.searchParams.get(param.name) != null
      : page.state[key] === true;

  // Push our history entry when the dialog opens; remove it when the dialog is
  // closed programmatically while our token is still current.
  $effect(() => {
    const open = isOpen();
    const here = present();

    if (open && !pushed && !adopted) {
      if (here) {
        // Opened from a token already in the URL (deep link / reload).
        adopted = true;
      } else if (param) {
        pushed = true;
        const url = new URL(page.url);
        url.searchParams.set(param.name, param.value());
        pushState(url, { ...page.state });
      } else {
        pushed = true;
        pushState("", { ...page.state, [key]: true });
      }
    } else if (!open && (pushed || adopted) && here) {
      if (pushed) {
        pushed = false;
        history.back();
      } else {
        adopted = false;
        const url = new URL(page.url);
        url.searchParams.delete(param!.name);
        replaceState(url, { ...page.state });
      }
    }
  });

  // Close the dialog when the user pops our token (back button / gesture):
  // SvelteKit reverts `page.state` / `page.url`, dropping it.
  $effect(() => {
    const here = present();
    if (!here && (pushed || adopted)) {
      pushed = false;
      adopted = false;
      if (isOpen()) close();
    }
  });
}
