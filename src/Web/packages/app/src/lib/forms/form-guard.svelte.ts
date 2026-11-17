import { beforeNavigate } from "$app/navigation";
import { flushSync } from "svelte";
import { SvelteSet } from "svelte/reactivity";
import type { z, ZodIssue } from "zod";
import { deepEqual } from "./deep-equal";
import { followUpError, genericSubmitError } from "./submit-messages.svelte";
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
  #snapshot: T | null = $state.raw(null);
  #issues: ZodIssue[] = $state([]);
  #touched: boolean = $state(false);
  #submitted: boolean = $state(false);
  #blurred = new SvelteSet<string>();
  #edited = new SvelteSet<string>();
  #submission: Submission;

  constructor(options: FormGuardOptions<T>) {
    this.#options = options;
    this.#submission = useSubmission({
      fallback: options.submitErrorMessage ?? genericSubmitError(),
      followUpFallback: followUpError(),
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

    $effect(() => {
      this.validate();
    });

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

  /** Every issue the schema reports, shown or not. `valid` reads this. */
  get issues(): ZodIssue[] {
    return this.#issues;
  }

  get valid(): boolean {
    return this.#issues.length === 0;
  }

  /** True once a submit has been attempted and until the form saves or resets. */
  get submitted(): boolean {
    return this.#submitted;
  }

  /** The issues to show: a field's once it was edited and left, all of them once a submit was attempted. */
  get visibleIssues(): ZodIssue[] {
    if (this.#submitted) return this.#issues;
    return this.#issues.filter(
      (issue) =>
        typeof issue.path[0] === "string" && this.#blurred.has(issue.path[0]),
    );
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

  /** The issues for one field that are due to show: see {@link visibleIssues}. */
  issuesFor(field: string): ZodIssue[] {
    return this.visibleIssues.filter((issue) => issue.path[0] === field);
  }

  /** The props a guarded `FormField` takes: its key, and the issues due to show. */
  field(path: string): { name: string; issues: ZodIssue[] } {
    return { name: path, issues: this.issuesFor(path) };
  }

  #clearVisibility(): void {
    this.#submitted = false;
    this.#blurred.clear();
    this.#edited.clear();
  }

  #fieldOf(event: Event): string | undefined {
    if (!(event.target instanceof Element)) return undefined;
    return event.target.closest<HTMLElement>("[data-field]")?.dataset.field;
  }

  #editField = (event: Event): void => {
    const field = this.#fieldOf(event);
    if (field != null) this.#edited.add(field);
  };

  #leaveField = (event: FocusEvent): void => {
    const field = this.#fieldOf(event);
    if (field == null || this.#snapshot == null) return;
    const changed = !deepEqual(this.#options.values()[field], this.#snapshot[field]);
    if (changed || this.#edited.has(field)) this.#blurred.add(field);
  };

  reset(): void {
    this.#touched = false;
    this.#clearVisibility();
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
    const enhanced = this.#options.form.enhance(
      async (helpers: { submit: () => Promise<boolean> }) => {
        this.#submission.clear();
        this.#submitted = true;

        if (!this.validate()) {
          flushSync();
          this.focusInvalid();
          return;
        }

        const succeeded = await this.#submission.run(helpers.submit, async () => {
          const updated = this.#options.initial();
          if (updated != null) {
            this.#snapshot = structuredClone(updated);
          }
          this.#clearVisibility();
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
    return {
      ...enhanced,
      oninput: this.#editField,
      onfocusout: this.#leaveField,
    };
  }
}
