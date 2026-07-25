import { beforeNavigate } from "$app/navigation";
import { Debounced } from "runed";
import type { z, ZodIssue } from "zod";
import { deepEqual } from "./deep-equal";
import { describeSubmitError, GENERIC_SUBMIT_ERROR } from "./submit-error";

export interface FormGuardOptions<T extends Record<string, unknown>> {
  form: any;
  schema: z.ZodType<T>;
  el: () => HTMLFormElement | null;
  initial: () => T | null | undefined;
  values: () => T;
  navBlockMessage?: string;
  onreset?: (snapshot: T) => void;
  /** Message shown when the submission fails for a reason with no user-facing text. */
  submitErrorMessage?: string;
}

export class FormGuard<T extends Record<string, unknown>> {
  #options: FormGuardOptions<T>;
  #snapshot: T | null = $state(null);
  #issues: ZodIssue[] = $state([]);
  #touched: boolean = $state(false);
  #submitted: boolean = $state(false);
  #submitError: string | null = $state(null);
  #debounced: Debounced<boolean>;

  constructor(options: FormGuardOptions<T>) {
    this.#options = options;

    // Snapshot from initial when truthy
    const initial = options.initial();
    if (initial != null) {
      this.#snapshot = structuredClone(initial);
    }

    // Watch initial() for deferred data loading
    $effect(() => {
      const val = options.initial();
      if (val != null && this.#snapshot == null) {
        this.#snapshot = structuredClone(val);
      }
    });

    // Set touched when dirty becomes true
    $effect(() => {
      if (this.dirty) {
        this.#touched = true;
      }
    });

    // Debounced validation
    this.#debounced = new Debounced(() => this.validate(), 300);

    // Navigation blocking
    if (options.navBlockMessage) {
      beforeNavigate((navigation: any) => {
        if (this.dirty && this.#touched) {
          if (!confirm(options.navBlockMessage!)) {
            navigation.cancel();
          }
        }
      });
    }
  }

  get dirty(): boolean {
    if (this.#snapshot == null) return false;
    return !deepEqual(this.#options.values(), this.#snapshot);
  }

  get touched(): boolean {
    return this.#touched;
  }

  get snapshot(): Readonly<T> | null {
    return this.#snapshot;
  }

  get issues(): ZodIssue[] {
    return this.#issues;
  }

  get valid(): boolean {
    return this.#issues.length === 0;
  }

  get submitted(): boolean {
    return this.#submitted;
  }

  /**
   * Set when the last submission was rejected by the server. Render it next to
   * the submit control — the form stays dirty and the entered values stay put.
   */
  get submitError(): string | null {
    return this.#submitError;
  }

  validate(): boolean {
    const result = this.#options.schema.safeParse(this.#options.values());
    if (result.success) {
      this.#issues = [];
      return true;
    }
    this.#issues = result.error.issues;
    return false;
  }

  debouncedValidate(): void {
    // Access .current to trigger the debounced evaluation
    this.#debounced.current;
  }

  issuesFor(field: string): ZodIssue[] {
    return this.#issues.filter((issue) => issue.path[0] === field);
  }

  reset(): void {
    this.#touched = false;
    this.#issues = [];
    this.#submitError = null;
    if (this.#snapshot != null && this.#options.onreset) {
      this.#options.onreset(structuredClone(this.#snapshot));
    }
  }

  focusInvalid(): void {
    const el = this.#options.el();
    if (!el) return;
    const invalid = el.querySelector<HTMLElement>('[aria-invalid="true"]');
    invalid?.focus();
  }

  /**
   * Wraps the form's `enhance` with client-side validation and dirty-state
   * bookkeeping. The consumer callback runs only after a successful submission.
   */
  enhance(
    callback?: (helpers: {
      submit: () => Promise<boolean>;
    }) => Promise<void>,
  ) {
    return this.#options.form.enhance(
      async (helpers: { submit: () => Promise<boolean> }) => {
        this.#submitError = null;

        // 1. Validate BEFORE submit
        if (!this.validate()) {
          this.focusInvalid();
          return;
        }

        // 2. Submit. A handler that throws (e.g. `error(400, …)`) rejects here;
        //    letting that propagate makes SvelteKit swap in the nearest error
        //    page, discarding everything the user typed.
        let succeeded: boolean;
        try {
          succeeded = await helpers.submit();
        } catch (err) {
          console.error("Form submission failed:", err);
          this.#submitError = describeSubmitError(
            err,
            this.#options.submitErrorMessage ?? GENERIC_SUBMIT_ERROR,
          );
          return;
        }

        // 3. `submit()` resolves false when the server returned validation
        //    issues. The form stays dirty so the values aren't lost, and the
        //    guard keeps blocking navigation.
        if (!succeeded) {
          this.focusInvalid();
          return;
        }

        // 4. Success: re-snapshot as clean and clear stale issues
        const updated = this.#options.initial();
        if (updated != null) {
          this.#snapshot = structuredClone(updated);
        }
        this.#submitted = true;
        this.#touched = false;
        this.#issues = [];

        // 5. Consumer callback
        await callback?.(helpers);
      },
    );
  }
}
