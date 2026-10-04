import { beforeNavigate } from "$app/navigation";
import { Debounced } from "runed";
import type { z, ZodIssue } from "zod";
import { deepEqual } from "./deep-equal";
import { FOLLOW_UP_ERROR, GENERIC_SUBMIT_ERROR } from "./submit-error";
import { useSubmission, type Submission } from "./submission.svelte";

/** The part of SvelteKit's `RemoteForm` the guard drives. */
export interface GuardedForm {
  enhance(
    callback: (helpers: { submit: () => Promise<boolean> }) => Promise<void>,
  ): {
    method: "POST";
    action: string;
    [attachment: symbol]: (node: HTMLFormElement) => void;
  };
}

export interface FormGuardOptions<T extends Record<string, unknown>> {
  form: GuardedForm;
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
  #submission: Submission;
  #debounced: Debounced<boolean>;

  constructor(options: FormGuardOptions<T>) {
    this.#options = options;
    this.#submission = useSubmission({
      fallback: options.submitErrorMessage ?? GENERIC_SUBMIT_ERROR,
      followUpFallback: FOLLOW_UP_ERROR,
    });

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
      beforeNavigate((navigation) => {
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
    return this.#submission.error;
  }

  /** See `Submission.saved`. */
  get saved(): number {
    return this.#submission.saved;
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
    void this.#debounced.current;
  }

  issuesFor(field: string): ZodIssue[] {
    return this.#issues.filter((issue) => issue.path[0] === field);
  }

  reset(): void {
    this.#touched = false;
    this.#issues = [];
    this.#submission.clear();
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
   * bookkeeping. The consumer callback runs only after a successful submission;
   * it returns false when a follow-up save failed, as `Submission.run`'s
   * `onSuccess` does.
   */
  enhance(
    callback?: (helpers: {
      submit: () => Promise<boolean>;
    }) => Promise<void | boolean>,
  ) {
    return this.#options.form.enhance(
      async (helpers: { submit: () => Promise<boolean> }) => {
        this.#submission.clear();

        if (!this.validate()) {
          this.focusInvalid();
          return;
        }

        const succeeded = await this.#submission.run(helpers.submit, async () => {
          const updated = this.#options.initial();
          if (updated != null) {
            this.#snapshot = structuredClone(updated);
          }
          this.#submitted = true;
          this.#touched = false;
          this.#issues = [];
          return callback?.(helpers);
        });

        // `submit()` resolves false when the server returned validation issues.
        // The form stays dirty so the values aren't lost, and the guard keeps
        // blocking navigation.
        if (!succeeded && this.#submission.error == null) this.focusInvalid();
      },
    );
  }
}
