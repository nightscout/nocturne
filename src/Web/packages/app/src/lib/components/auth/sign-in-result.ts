import { invalidateAll } from "$app/navigation";
import { setAuthCookies } from "#routes/(unauthenticated)/auth/auth.remote.js";

/** What the passkey completion endpoint answers with. */
export interface SignInResult {
  success?: boolean;
  accessToken?: string;
  refreshToken?: string;
  expiresIn?: number;
  refreshExpiresIn?: number;
  totpRequired?: boolean;
  stepUpToken?: string | null;
  error?: string;
}

export type SignInOutcome =
  | { state: "refused"; message: string }
  /** The passkey was accepted but the account also has an authenticator, so no session exists yet. */
  | { state: "second-factor"; stepUpToken: string }
  | { state: "signed-in" };

/**
 * Turns a completed passkey assertion into a session. A signed-in outcome has had its cookies
 * validated and the page data invalidated; the caller decides where to go next.
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

  if (result.accessToken) {
    await setAuthCookies({
      accessToken: result.accessToken,
      refreshToken: result.refreshToken,
      expiresIn: result.expiresIn,
      refreshExpiresIn: result.refreshExpiresIn,
    });
  }

  await invalidateAll();
  return { state: "signed-in" };
}
