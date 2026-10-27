const PLACEHOLDER_ORIGIN = "http://return-url.invalid";

/**
 * Reduces a caller-supplied `returnUrl` to a same-origin path.
 *
 * Sign-in forms carry the destination in a hidden field, so the value is
 * attacker-controllable: anything that isn't a single-slash-rooted path is
 * replaced with the fallback, which blocks protocol-relative (`//evil.test`),
 * absolute, backslash-smuggled and whitespace-smuggled redirects. A path whose
 * dot segments (plain or percent-encoded) resolve to `//host` is refused too:
 * anything that rebuilds the path from its parsed form would turn it into a
 * protocol-relative redirect.
 */
export function safeReturnUrl(value: unknown, fallback = "/"): string {
  if (typeof value !== "string") return fallback;

  const trimmed = value.trim();
  if (trimmed === "") return fallback;
  if (!trimmed.startsWith("/")) return fallback;
  // "//host" and "/\host" are treated as absolute by browsers.
  if (trimmed.startsWith("//") || trimmed.startsWith("/\\")) return fallback;
  if (trimmed.includes("\\")) return fallback;
  // The URL parser strips tab and newline, so "/\t/evil.test" resolves as "//evil.test".
  // eslint-disable-next-line no-control-regex -- deliberately rejects control characters
  if (/[\u0000-\u001F\u007F\s]/.test(trimmed)) return fallback;

  const resolved = new URL(trimmed, PLACEHOLDER_ORIGIN);
  if (resolved.origin !== PLACEHOLDER_ORIGIN) return fallback;
  if (resolved.pathname.startsWith("//")) return fallback;

  return trimmed;
}
