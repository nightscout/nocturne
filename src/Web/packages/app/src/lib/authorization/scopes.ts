/**
 * Scope checks against the viewer's granted scopes
 * (`page.data.effectivePermissions`, the API's `GET /api/v4/me/permissions`).
 *
 * A mirror of the server's `Scope.Satisfies`, which is the predicate
 * `RequireScopeAttribute` evaluates: full access satisfies everything, an atom
 * satisfies itself, a readwrite atom satisfies its read counterpart, and
 * `audit.manage` satisfies `audit.read`. A check here only decides what to
 * offer; the endpoint still answers on the server's own terms.
 */

const FULL_ACCESS = "*";

/**
 * Implications beyond the readwrite-to-read rule: for a required scope, the
 * granted scopes that satisfy it without matching it. Mirrors the entry the
 * server's `Scope.SatisfiedBy` carries beyond the readwrite pairs.
 */
const EXTRA_IMPLICATIONS: Readonly<Record<string, readonly string[]>> = {
  "audit.read": ["audit.manage"],
};

/** Whether `granted` covers `required`. */
export function satisfiesScope(
  granted: readonly string[],
  required: string
): boolean {
  if (granted.includes(FULL_ACCESS) || granted.includes(required)) return true;
  if (
    required.endsWith(".read") &&
    granted.includes(`${required.slice(0, -".read".length)}.readwrite`)
  )
    return true;
  return (
    EXTRA_IMPLICATIONS[required]?.some((scope) => granted.includes(scope)) ??
    false
  );
}

/** Whether `granted` covers every scope in `required`. */
export function satisfiesAllScopes(
  granted: readonly string[],
  required: readonly string[]
): boolean {
  return required.every((scope) => satisfiesScope(granted, scope));
}
