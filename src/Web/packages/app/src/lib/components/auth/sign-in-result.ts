import { loginComplete } from "#lib/api/generated/passkeys.generated.remote.js";
import { setAuthCookies } from "#routes/(unauthenticated)/auth/auth.remote.js";
import type { AssertionSubmission } from "./passkey-login";

/**
 * Submits a passkey sign-in whose answer goes to {@link settleSignIn}, which hands any first
 * recovery codes to a caller that shows them.
 */
export function completePasskeySignIn(assertion: AssertionSubmission) {
  return loginComplete({ ...assertion, showsRecoveryCodes: true });
}

/** What a passkey sign-in or the setup owner completion answers with. */
export interface SignInResult {
  success?: boolean;
  accessToken?: string;
  refreshToken?: string;
  expiresIn?: number;
  refreshExpiresIn?: number;
  totpRequired?: boolean;
  stepUpToken?: string | null;
  recoveryCodes?: string[] | null;
  error?: string;
}

export type SignInOutcome =
  | { state: "refused"; message: string }
  /** The passkey was accepted but the account also has an authenticator, so no session exists yet. */
  | { state: "second-factor"; stepUpToken: string }
  /**
   * `recoveryCodes` holds the member's first codes when this sign-in issued them, and is empty
   * otherwise. The API shows them nowhere else, so they are shown before the caller moves on.
   */
  | { state: "signed-in"; recoveryCodes: string[] };

/**
 * Turns a completed passkey ceremony into a session. A signed-in outcome has had its cookies
 * validated, unless it carries recovery codes: a cookie failure then still shows the codes, since
 * the API shows them nowhere else. The page data is not refreshed here: the caller refreshes it as
 * it moves on, which waits while recovery codes are on screen, since a refresh would carry a
 * signed-in page away.
 */
export async function settleSignIn(result: SignInResult): Promise<SignInOutcome> {
  if (!result.success) {
    return { state: "refused", message: result.error ?? "Could not sign you in. Try again." };
  }

  if (result.totpRequired) {
    return result.stepUpToken
      ? { state: "second-factor", stepUpToken: result.stepUpToken }
      : { state: "refused", message: "Could not sign you in. Try again." };
  }

  const recoveryCodes = result.recoveryCodes ?? [];

  if (result.accessToken) {
    try {
      await setAuthCookies({
        accessToken: result.accessToken,
        refreshToken: result.refreshToken,
        expiresIn: result.expiresIn,
        refreshExpiresIn: result.refreshExpiresIn,
      });
    } catch (err) {
      // The codes are stored and this answer is the only copy, so they are shown all the same.
      if (recoveryCodes.length === 0) throw err;
    }
  }

  return { state: "signed-in", recoveryCodes };
}
