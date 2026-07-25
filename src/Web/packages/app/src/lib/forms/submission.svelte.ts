import { describeSubmitError } from "./submit-error";

export interface Submission {
  /** Why the last attempt failed, or null. Render it with FormError. */
  readonly error: string | null;
  clear(): void;
  /**
   * Runs a form's `submit()` and turns a rejection into {@link error}.
   *
   * @param submit The `submit` helper from the form's `enhance` callback.
   * @param onSuccess Runs only when the submission succeeded.
   * @returns Whether the submission succeeded.
   */
  run(
    submit: () => Promise<boolean>,
    onSuccess?: () => void | Promise<void>
  ): Promise<boolean>;
}

/**
 * Failure handling for a `form()` remote function's `enhance` callback.
 *
 * A handler that throws rejects `submit()`, and an uncaught rejection inside an
 * enhance callback makes SvelteKit replace the page with its error page — on a
 * sign-in form that means the user loses what they typed and gets no reason
 * why. Wrapping the call keeps them on the page with a message they can act on.
 */
export function useSubmission(options?: { fallback?: string }): Submission {
  let error = $state<string | null>(null);

  return {
    get error() {
      return error;
    },
    clear() {
      error = null;
    },
    async run(submit, onSuccess) {
      error = null;
      try {
        const succeeded = await submit();
        if (succeeded) await onSuccess?.();
        return succeeded;
      } catch (err) {
        console.error("Form submission failed:", err);
        error = describeSubmitError(err, options?.fallback);
        return false;
      }
    },
  };
}
