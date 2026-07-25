/**
 * Reduces a caller-supplied `returnUrl` to a same-origin path.
 *
 * Sign-in forms carry the destination in a hidden field, so the value is
 * attacker-controllable: anything that isn't a single-slash-rooted path is
 * replaced with the fallback, which blocks protocol-relative (`//evil.test`),
 * absolute and backslash-smuggled redirects.
 */
export function safeReturnUrl(value: unknown, fallback = "/"): string {
  if (typeof value !== "string") return fallback;

  const trimmed = value.trim();
  if (trimmed === "") return fallback;
  if (!trimmed.startsWith("/")) return fallback;
  // "//host" and "/\host" are treated as absolute by browsers.
  if (trimmed.startsWith("//") || trimmed.startsWith("/\\")) return fallback;
  if (trimmed.includes("\\")) return fallback;

  return trimmed;
}
