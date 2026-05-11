import { beforeNavigate } from "$app/navigation";
import { Debounced } from "runed";
import type { z, ZodIssue } from "zod";
import { deepEqual } from "./deep-equal";

export interface FormGuardOptions<T extends Record<string, unknown>> {
  form: any;
  schema: z.ZodType<T>;
  el: () => HTMLFormElement | null;
  initial: () => T | null | undefined;
  values: () => T;
  navBlockMessage?: string;
  onreset?: (snapshot: T) => void;
}

export class FormGuard<T extends Record<string, unknown>> {
  #options: FormGuardOptions<T>;
  #snapshot: T | null = $state(null);
  #issues: ZodIssue[] = $state([]);
  #touched: boolean = $state(false);
  #submitted: boolean = $state(false);
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

  enhance(
    callback?: (helpers: {
      submit: () => Promise<void>;
    }) => Promise<void>,
  ) {
    return this.#options.form.enhance(
      async (helpers: { submit: () => Promise<void> }) => {
        // 1. Validate BEFORE submit
        if (!this.validate()) {
          this.focusInvalid();
          return;
        }

        // 2. Submit
        await helpers.submit();

        // 3. On success, re-snapshot and reset state
        if (this.#options.form.result) {
          const updated = this.#options.initial();
          if (updated != null) {
            this.#snapshot = structuredClone(updated);
          }
          this.#submitted = true;
          this.#touched = false;
          this.#issues = [];
        }

        // 4. Consumer callback
        await callback?.(helpers);
      },
    );
  }
}
