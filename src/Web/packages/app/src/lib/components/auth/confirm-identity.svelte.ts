import { browser } from "$app/env";
import { isSignInRequired } from "#lib/forms/submit-error.js";

/**
 * The one "confirm it is you" prompt. {@link ConfirmIdentityDialog} renders it, mounted once in the
 * root layout; a gated surface opens it with {@link request}, a refused write with
 * {@link askAfterRefusal}.
 */
class ConfirmIdentityPrompt {
  open = $state(false);
  #settle: ((signedIn: boolean) => void) | null = null;

  /** Opens the prompt. Resolves true once the person signed in, false when they closed it. */
  request(): Promise<boolean> {
    if (!browser) return Promise.resolve(false);

    this.#settle?.(false);
    this.open = true;
    return new Promise((resolve) => {
      this.#settle = resolve;
    });
  }

  /**
   * Opens the prompt when a write the person just asked for was refused with `sign_in_required`.
   * Call it where the write was submitted, never while rendering: it writes state. A read refused
   * the same way opens nothing, so a panel loading on its own never opens a dialog.
   * @returns Whether it was that refusal.
   */
  askAfterRefusal(err: unknown): boolean {
    if (!isSignInRequired(err)) return false;
    void this.request();
    return true;
  }

  /** Closes the prompt and answers the caller that opened it. */
  settle(signedIn: boolean): void {
    this.open = false;
    const settle = this.#settle;
    this.#settle = null;
    settle?.(signedIn);
  }
}

export const confirmIdentity = new ConfirmIdentityPrompt();
