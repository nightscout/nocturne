/**
 * Scope checks against the viewer's granted scopes
 * (`page.data.effectivePermissions`, the API's `GET /api/v4/me/permissions`).
 *
 * A partial mirror of the server's `Scope.Satisfies`, which is the predicate
 * `RequireScopeAttribute` evaluates: full access satisfies everything, an atom
 * satisfies itself, and a readwrite atom satisfies its read counterpart. The
 * server's one further implication — `audit.manage` satisfies `audit.read` — is
 * not mirrored, because no navigation decision asks for `audit.read`. A check
 * here only decides what to offer; the endpoint still answers on the server's
 * own terms.
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
