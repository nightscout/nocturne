import { AUTH_COOKIE_NAMES } from "../config/auth-cookies";

/**
 * Minimal subset of SvelteKit's Cookies interface we need for propagation.
 * Kept narrow so this module can be unit-tested without importing SvelteKit.
 */
export interface CookieSetter {
  set(
    name: string,
    value: string,
    opts: CookieSetOptions & { path: string }
  ): void;
  delete(name: string, opts: { path: string; domain?: string }): void;
}

export interface CookieSetOptions {
  httpOnly?: boolean;
  secure?: boolean;
  sameSite?: "lax" | "strict" | "none" | boolean;
  maxAge?: number;
  expires?: Date;
  domain?: string;
}

const AUTH_COOKIE_SET: ReadonlySet<string> = new Set([
  AUTH_COOKIE_NAMES.accessToken,
  AUTH_COOKIE_NAMES.refreshToken,
  AUTH_COOKIE_NAMES.isAuthenticated,
]);

/**
 * Propagate auth-related Set-Cookie headers from a server-to-server fetch
 * response onto the outgoing SvelteKit response cookies, so the browser
 * actually receives rotated tokens after an internal session refresh.
 *
 * Without this, SSR-initiated calls (authHandle, remote functions, load
 * functions) trigger token rotation on the backend but the rotated cookies
 * never make it back to the browser — causing the next request to fail
 * authentication and redirect to /auth/login.
 *
 * Only auth cookies are propagated; unrelated Set-Cookie headers from the
 * backend are ignored.
 */
export function propagateAuthCookies(
  setCookieHeaders: readonly string[],
  cookies: CookieSetter
): void {
  for (const header of setCookieHeaders) {
    const parsed = parseSetCookieHeader(header);
    if (!parsed) continue;
    if (!AUTH_COOKIE_SET.has(parsed.name)) continue;

    if (parsed.isDeletion) {
      cookies.delete(parsed.name, {
        path: parsed.path ?? "/",
        ...(parsed.domain !== undefined ? { domain: parsed.domain } : {}),
      });
      continue;
    }

    const opts: CookieSetOptions & { path: string } = {
      path: parsed.path ?? "/",
    };
    if (parsed.httpOnly !== undefined) opts.httpOnly = parsed.httpOnly;
    if (parsed.secure !== undefined) opts.secure = parsed.secure;
    if (parsed.sameSite !== undefined) opts.sameSite = parsed.sameSite;
    if (parsed.maxAge !== undefined) opts.maxAge = parsed.maxAge;
    if (parsed.expires !== undefined) opts.expires = parsed.expires;
    if (parsed.domain !== undefined) opts.domain = parsed.domain;

    cookies.set(parsed.name, parsed.value, opts);
  }
}

interface ParsedSetCookie {
  name: string;
  value: string;
  path?: string;
  domain?: string;
  httpOnly?: boolean;
  secure?: boolean;
  sameSite?: "lax" | "strict" | "none";
  maxAge?: number;
  expires?: Date;
  isDeletion: boolean;
}

function parseSetCookieHeader(header: string): ParsedSetCookie | null {
  const parts = header.split(";").map((p) => p.trim());
  const first = parts.shift();
  if (!first) return null;

  const eqIdx = first.indexOf("=");
  if (eqIdx < 0) return null;

  const name = first.slice(0, eqIdx).trim();
  const value = first.slice(eqIdx + 1).trim();
  if (!name) return null;

  const result: ParsedSetCookie = { name, value, isDeletion: false };

  for (const part of parts) {
    if (!part) continue;
    const eq = part.indexOf("=");
    const rawKey = eq < 0 ? part : part.slice(0, eq);
    const val = eq < 0 ? "" : part.slice(eq + 1).trim();
    const key = rawKey.trim().toLowerCase();

    switch (key) {
      case "path":
        result.path = val;
        break;
      case "domain":
        result.domain = val;
        break;
      case "httponly":
        result.httpOnly = true;
        break;
      case "secure":
        result.secure = true;
        break;
      case "samesite": {
        const normalized = val.toLowerCase();
        if (
          normalized === "lax" ||
          normalized === "strict" ||
          normalized === "none"
        ) {
          result.sameSite = normalized;
        }
        break;
      }
      case "max-age": {
        const n = Number.parseInt(val, 10);
        if (!Number.isNaN(n)) result.maxAge = n;
        break;
      }
      case "expires": {
        const exp = new Date(val);
        if (!Number.isNaN(exp.getTime())) result.expires = exp;
        break;
      }
    }
  }

  // Treat as deletion if the server has explicitly expired the cookie:
  // empty value plus either Max-Age<=0 or an Expires in the past.
  const now = Date.now();
  const expiredByMaxAge = result.maxAge !== undefined && result.maxAge <= 0;
  const expiredByExpires =
    result.expires !== undefined && result.expires.getTime() <= now;
  if (value === "" && (expiredByMaxAge || expiredByExpires)) {
    result.isDeletion = true;
  }

  return result;
}
