import { Debounced } from "runed";

/** The shape of a remote query used to check availability. */
export interface AvailabilityQuery {
  readonly loading: boolean;
  readonly error?: unknown;
  readonly current?: { isValid?: boolean; message?: string | null } | null;
}

export interface AvailabilityOptions {
  /** Field name used in the generated messages, e.g. "Slug" or "Username". */
  label: string;
  /** Shorter values are rejected without asking the server. */
  minLength?: number;
  /** How long to wait after the last keystroke before checking. */
  debounceMs?: number;
}

export interface Availability {
  /** A check is in flight, or one is about to start. */
  readonly validating: boolean;
  /** Why the value can't be used, or null. */
  readonly error: string | null;
  /** The server confirmed this value is available. */
  readonly valid: boolean;
}

interface AvailabilityState {
  validating: boolean;
  error: string | null;
  valid: boolean;
}

const IDLE: AvailabilityState = { validating: false, error: null, valid: false };

/**
 * Debounced "is this name free?" checking for a single text field.
 *
 * Derived, not effect-driven: nothing is written on a keystroke, so the state
 * can't lag the input. An empty value is idle (no error) — pair it with the
 * submit button's own disabled state rather than nagging before the user types.
 *
 * @param value Reads the current (already normalised) field value.
 * @param check Creates the availability query for a value. Called from a derived,
 * so a remote query function can be passed directly — repeat calls with the same
 * value reuse the same request.
 */
export function useAvailability(
  value: () => string,
  check: (value: string) => AvailabilityQuery,
  { label, minLength = 3, debounceMs = 400 }: AvailabilityOptions
): Availability {
  const debounced = new Debounced(value, debounceMs);

  const state = $derived.by<AvailabilityState>(() => {
    const current = value();
    if (!current) return IDLE;

    if (current.length < minLength) {
      return {
        validating: false,
        error: `${label} must be at least ${minLength} characters`,
        valid: false,
      };
    }

    // Still waiting for the debounce to settle on the latest keystroke.
    if (debounced.current !== current) {
      return { validating: true, error: null, valid: false };
    }

    const result = check(current);

    // loading: request in flight; !current: result not populated yet
    if (result.loading || !result.current) {
      return { validating: true, error: null, valid: false };
    }

    if (result.error) {
      return {
        validating: false,
        error: `Could not validate ${label.toLowerCase()}`,
        valid: false,
      };
    }

    if (result.current.isValid) {
      return { validating: false, error: null, valid: true };
    }

    return {
      validating: false,
      error: result.current.message ?? `Invalid ${label.toLowerCase()}`,
      valid: false,
    };
  });

  return {
    get validating() {
      return state.validating;
    },
    get error() {
      return state.error;
    },
    get valid() {
      return state.valid;
    },
  };
}
