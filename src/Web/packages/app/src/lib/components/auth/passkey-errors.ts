/**
 * Turns a failed passkey ceremony into something a person can act on.
 *
 * The browser's own messages leak through otherwise: a cancelled or timed-out
 * prompt surfaces the raw `NotAllowedError` text, and an empty options blob
 * surfaces "Unexpected end of JSON input". Callers pass the error here and log
 * the original to the console.
 */

import { describeSubmitError } from "$lib/forms/submit-error";

/** Raised when the server didn't return a usable options blob. */
export class CeremonyOptionsError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "CeremonyOptionsError";
  }
}

/**
 * Parses the WebAuthn options the server issued for a ceremony.
 *
 * @throws {CeremonyOptionsError} when the blob is missing, blank or not JSON.
 */
export function parseCeremonyOptions<T = Record<string, unknown>>(
  raw: string | null | undefined
): T {
  if (!raw || raw.trim() === "") {
    throw new CeremonyOptionsError("The server returned no passkey options");
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw new CeremonyOptionsError("The passkey options could not be read");
  }

  if (typeof parsed !== "object" || parsed === null) {
    throw new CeremonyOptionsError("The passkey options were not an object");
  }

  return parsed as T;
}

/** Which ceremony failed — the wording differs between the two. */
export type PasskeyCeremony = "login" | "register";

const CANCELLED: Record<PasskeyCeremony, string> = {
  login:
    "No passkey was used — try again, or sign in with a recovery code or your authenticator app.",
  register:
    "No passkey was created — try again when your device prompts you.",
};

const ALREADY_REGISTERED =
  "This device already has a passkey for that account. Try signing in with it instead.";

const CANNOT_BE_USED =
  "That passkey can't be used here. Try another sign-in method.";

const CLOSED_EARLY =
  "The passkey prompt closed before it finished. Please try again.";

const BLOCKED =
  "Your browser blocked the passkey prompt for this address. Check the address is correct and try again.";

const UNSUPPORTED =
  "This device can't be used to create a passkey. Use an authenticator app or a recovery code instead.";

const OPTIONS_FAILED =
  "We couldn't start the passkey step. Please try again in a moment.";

/**
 * @param err The thrown value.
 * @param ceremony Whether the user was signing in or creating a passkey.
 * @param fallback Shown when nothing more specific applies.
 */
export function describePasskeyError(
  err: unknown,
  ceremony: PasskeyCeremony,
  fallback = "We couldn't complete that step. Please try again."
): string {
  if (err instanceof CeremonyOptionsError) return OPTIONS_FAILED;

  if (err && typeof err === "object" && "name" in err) {
    const { name } = err;
    switch (name) {
      // Cancelled, dismissed, or timed out — indistinguishable by design, so
      // the platform doesn't reveal whether a credential exists.
      case "NotAllowedError":
        return CANCELLED[ceremony];
      case "InvalidStateError":
        return ceremony === "register" ? ALREADY_REGISTERED : CANNOT_BE_USED;
      case "AbortError":
        return CLOSED_EARLY;
      case "SecurityError":
        return BLOCKED;
      case "NotSupportedError":
      case "ConstraintError":
        return UNSUPPORTED;
    }
  }

  // Errors from our own server: a 4xx message is written for the user.
  return describeSubmitError(err, fallback);
}
