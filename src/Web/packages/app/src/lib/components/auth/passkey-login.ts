/**
 * The WebAuthn half of signing in with a passkey, shared by the three ways in: the discoverable
 * button, the username form, and the browser's autofill dropdown.
 */

import {
  browserSupportsWebAuthnAutofill,
  startAuthentication,
  type PublicKeyCredentialRequestOptionsJSON,
} from "@simplewebauthn/browser";
import { parseCeremonyOptions } from "./passkey-errors";

/** What every passkey-options endpoint answers with. */
export interface CeremonyOptionsResponse {
  options?: string | null;
  challengeToken?: string | null;
}

export interface AssertionSubmission {
  assertionResponseJson: string;
  challengeToken: string;
}

/**
 * Fetch a challenge, run the assertion, and submit it.
 *
 * @param useBrowserAutofill Offer the passkey in the autofill dropdown rather than prompting
 * straight away. Requires an `<input>` whose `autocomplete` ends in `webauthn` to be on the page,
 * and resolves only if the visitor picks the passkey from that dropdown.
 */
export async function runPasskeyAssertion<T>(
  requestOptions: () => Promise<CeremonyOptionsResponse>,
  submit: (assertion: AssertionSubmission) => Promise<T>,
  useBrowserAutofill = false
): Promise<T> {
  const response = await requestOptions();
  const optionsJSON =
    parseCeremonyOptions<PublicKeyCredentialRequestOptionsJSON>(
      response.options
    );

  const assertion = await startAuthentication({ optionsJSON, useBrowserAutofill });

  return submit({
    assertionResponseJson: JSON.stringify(assertion),
    challengeToken: response.challengeToken ?? "",
  });
}

/**
 * Run `assertion` only where the browser can put a passkey in the autofill dropdown.
 *
 * Conditional mediation needs a challenge fetched before the visitor has done anything, so the
 * guard comes first: a browser without it must not be asked for one, and must not have a
 * ceremony started that would block the explicit buttons.
 */
export async function offerPasskeyInAutofill<T>(
  assertion: () => Promise<T>,
  isAvailable: () => Promise<boolean> = browserSupportsWebAuthnAutofill
): Promise<T | null> {
  if (!(await isAvailable())) return null;
  return assertion();
}
