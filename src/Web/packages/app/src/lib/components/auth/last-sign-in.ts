/**
 * The sign-in method that last worked, so the login form can lead with it.
 *
 * The API writes the hint alongside the session cookies (see
 * `SessionCookieExtensions.SetLastSignInCookie`), which is the only place that knows the Domain
 * the session is scoped to — a hint written on one tenant's subdomain has to be readable on the
 * apex and on sibling tenants. This side only reads it. The value is `passkey`, or `oidc:` and
 * the provider's id; it names no person and no account.
 */

/** Methods the hint can name. A value outside this set is treated as no hint at all. */
export const SIGN_IN_METHODS = ["passkey", "oidc"] as const;

export type SignInMethod = (typeof SIGN_IN_METHODS)[number];

export interface LastSignIn {
  method: SignInMethod;
  /** The identity provider, for `oidc`. Null for every other method. */
  providerId: string | null;
}

/** Anything with an id, so this works on the generated provider type without redeclaring it. */
interface Identified {
  id?: string | null;
}

/**
 * Read the hint cookie's value. Returns null for an absent, empty or unrecognised value, so a
 * hint from a future version degrades to the default ordering rather than hiding a button.
 */
export function parseLastSignIn(
  value: string | null | undefined
): LastSignIn | null {
  if (!value) return null;

  const separator = value.indexOf(":");
  const method = separator < 0 ? value : value.slice(0, separator);
  if (!isSignInMethod(method)) return null;

  const providerId = separator < 0 ? "" : value.slice(separator + 1).trim();
  return { method, providerId: providerId.length > 0 ? providerId : null };
}

/**
 * Whether the hint names this control. An `oidc` hint names one provider, so it matches only
 * that button; every other method has a single button and matches on the method alone.
 */
export function isLastUsed(
  hint: LastSignIn | null | undefined,
  method: SignInMethod,
  providerId?: string | null
): boolean {
  if (!hint || hint.method !== method) return false;
  if (method !== "oidc") return true;
  if (!hint.providerId || !providerId) return false;
  return hint.providerId.toLowerCase() === providerId.toLowerCase();
}

/**
 * The providers with the last-used one first. Their configured order is otherwise untouched, so
 * the list a visitor with no hint sees is the one the operator ordered.
 */
export function withLastUsedFirst<T extends Identified>(
  providers: readonly T[],
  hint: LastSignIn | null | undefined
): T[] {
  const index = providers.findIndex((provider) =>
    isLastUsed(hint, "oidc", provider.id)
  );
  if (index <= 0) return [...providers];

  const ordered = [...providers];
  const [lastUsed] = ordered.splice(index, 1);
  return [lastUsed!, ...ordered];
}

function isSignInMethod(value: string): value is SignInMethod {
  return SIGN_IN_METHODS.some((method) => method === value);
}
