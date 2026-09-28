import { describeSubmitError } from "./submit-error";

export interface Submission {
  /** Why the last attempt failed, or null. Render it with FormError. */
  readonly error: string | null;
  /**
   * Counts the submissions that succeeded. Pass it to `SubmitButton`'s `saved`,
   * which plays its saved moment each time the count moves.
   */
  readonly saved: number;
  clear(): void;
  /**
   * Runs a form's `submit()` and turns a rejection into {@link error}.
   *
   * @param submit The `submit` helper from the form's `enhance` callback.
   * @param onSuccess Runs only when the submission succeeded. Returning false
   *   says a follow-up save failed and has shown its own error, so the save is
   *   not counted in {@link saved}. A throw is not counted either, and says
   *   `followUpFallback` when no reason came with it.
   * @returns Whether the submission succeeded.
   */
  run(
    submit: () => Promise<boolean>,
    onSuccess?: () => void | boolean | Promise<void | boolean>
  ): Promise<boolean>;
}

/** For a form whose `onSuccess` saves something more after the record itself. */
export const FOLLOW_UP_ERROR =
  "Your changes were saved, but the step after saving didn't finish. Please try again.";

/**
 * Failure handling for a `form()` remote function's `enhance` callback.
 *
 * A handler that throws rejects `submit()`, and an uncaught rejection inside an
 * enhance callback makes SvelteKit replace the page with its error page — on a
 * sign-in form that means the user loses what they typed and gets no reason
 * why. Wrapping the call keeps them on the page with a message they can act on.
 */
export function useSubmission(options?: {
  fallback?: string;
  /** What a throw from `onSuccess` says. Defaults to {@link fallback}. */
  followUpFallback?: string;
}): Submission {
  let error = $state<string | null>(null);
  let saved = $state(0);

  return {
    get error() {
      return error;
    },
    get saved() {
      return saved;
    },
    clear() {
      error = null;
    },
    async run(submit, onSuccess) {
      error = null;
      let succeeded: boolean;
      try {
        succeeded = await submit();
      } catch (err) {
        console.error("Form submission failed:", err);
        error = describeSubmitError(err, options?.fallback);
        return false;
      }
      if (!succeeded) return false;
      try {
        if ((await onSuccess?.()) !== false) saved++;
      } catch (err) {
        console.error("Follow-up after a successful submission failed:", err);
        error = describeSubmitError(err, options?.followUpFallback ?? options?.fallback);
      }
      return true;
    },
  };
}
