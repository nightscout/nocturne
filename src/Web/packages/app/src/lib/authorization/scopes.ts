/**
 * Scope checks against the viewer's granted scopes
 * (`page.data.effectivePermissions`, the API's `GET /api/v4/me/permissions`).
 * Mirrors the server's `OAuthScopes.SatisfiesScope`, which is what
 * `[RequireScope]` evaluates: full access covers everything and a readwrite
 * grant covers its read counterpart.
 */

const FULL_ACCESS = "*";

/** Whether `granted` covers `required`. */
export function satisfiesScope(
  granted: readonly string[],
  required: string
): boolean {
  if (granted.includes(FULL_ACCESS) || granted.includes(required)) return true;
  return (
    required.endsWith(".read") &&
    granted.includes(`${required.slice(0, -".read".length)}.readwrite`)
  );
}
