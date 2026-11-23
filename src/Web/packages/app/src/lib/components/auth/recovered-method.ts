const SETTINGS_PATH = "/settings/account";
const RECOVERED_PARAM = "recovered";
const PASSKEY_PARAM = "credentialId";
const IDENTITY_PARAM = "linkedIdentity";

/** The sign-in method a recovery just added, as the settings page learns it from the address. */
export interface RecoveredMethod {
  passkeyId?: string;
  identityId?: string;
}

/**
 * The account settings address a recovery ends on. A passkey names itself here; the API appends
 * `linkedIdentity` to the same address when a provider identity was added.
 */
export function recoveredDestination(passkeyId?: string | null): string {
  const params = new URLSearchParams({ [RECOVERED_PARAM]: "1" });
  if (passkeyId) params.set(PASSKEY_PARAM, passkeyId);
  return `${SETTINGS_PATH}?${params}`;
}

/** The method named by a recovery's landing address, or null when the visit was not one. */
export function readRecoveredMethod(url: { searchParams: { get(name: string): string | null } }): RecoveredMethod | null {
  if (url.searchParams.get(RECOVERED_PARAM) !== "1") return null;

  return {
    passkeyId: url.searchParams.get(PASSKEY_PARAM) ?? undefined,
    identityId: url.searchParams.get(IDENTITY_PARAM) ?? undefined,
  };
}

/** The address without the parameters {@link readRecoveredMethod} reads. */
export function withoutRecoveredMethod(url: { pathname: string; search: string; hash: string }): string {
  const rest = new URLSearchParams(url.search);
  for (const name of [RECOVERED_PARAM, PASSKEY_PARAM, IDENTITY_PARAM]) rest.delete(name);
  const search = rest.size > 0 ? `?${rest}` : "";
  return url.pathname + search + url.hash;
}
