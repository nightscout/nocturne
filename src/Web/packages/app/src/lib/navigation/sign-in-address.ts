import { safeReturnUrl } from "#lib/navigation/safe-return-url.js";

const SIGN_IN_PATH = "/auth/login";

/**
 * The sign-in address that returns to `returnTo` afterwards. A `returnTo` that is not a
 * same-origin path (see {@link safeReturnUrl}) is dropped, leaving the bare sign-in address.
 */
export function signInAddress(returnTo?: string): string {
  const returnUrl = safeReturnUrl(returnTo, "");
  return returnUrl === "" ? SIGN_IN_PATH : `${SIGN_IN_PATH}?returnUrl=${encodeURIComponent(returnUrl)}`;
}
