/**
 * What this browser keeps for one member of one tenant that holds their health data (a custom
 * report's unsaved changes, the signals they starred): every such key starts with
 * `MEMBER_STORAGE_PREFIX` and names the tenant and the member, so it is cleared by prefix with no
 * knowledge of what it holds. It is removed when the member's session ends, and only on an answer
 * that it did end: a sign-out that went through (`SIGNED_OUT_COOKIE`), a session the server says is
 * gone, or the sign-in page once it knows nobody is signed in. An error on the way never clears it.
 */
import { browser } from "$app/env";

export const MEMBER_STORAGE_PREFIX = "nocturne.member.";

/**
 * Set by the sign-out endpoint on its response, so the next page this browser loads on the tenant
 * clears what the member kept. The sign-out lands on another page or the sign-in provider's, which
 * cannot clear this origin's storage; a sign-out the member cancels at the browser's leave prompt
 * never reaches the endpoint, so it keeps everything.
 */
export const SIGNED_OUT_COOKIE = "nocturne-signed-out";

/** Who kept a key: the tenant's slug and the member's subject id. */
export interface MemberOwner {
  tenant: string;
  subject: string;
}

/**
 * The owner of the keys read and written for the page's signed-in member. A guest session has no
 * subject of its own (every guest on a browser would share one), so it has none here.
 */
export function memberOwner(data: Record<string, unknown>): MemberOwner {
  const tenant = data.tenantSlug;
  const user = data.user;
  const subject = user && typeof user === "object" && "subjectId" in user ? user.subjectId : null;
  return {
    tenant: typeof tenant === "string" ? tenant : "",
    subject: data.isGuestSession !== true && typeof subject === "string" ? subject : "",
  };
}

/** The storage key of `name` for `owner`, or null when the owner is not one member of one tenant. */
export function memberKey(owner: MemberOwner, name: string): string | null {
  if (!owner.tenant || !owner.subject) return null;
  return `${MEMBER_STORAGE_PREFIX}${name}.${owner.tenant}.${owner.subject}`;
}

/** Removes every member key whose name starts with `name` (all of them when absent). */
export function clearMemberKeys(name = "", drop: (raw: string | null) => boolean = () => true): void {
  const prefix = `${MEMBER_STORAGE_PREFIX}${name}`;
  try {
    const keys: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.startsWith(prefix)) keys.push(key);
    }
    for (const key of keys) if (drop(localStorage.getItem(key))) localStorage.removeItem(key);
  } catch {
    // Storage cannot be read; there is nothing to clear.
  }
}

/** Removes what this browser keeps for every member of every tenant on this origin. */
export function clearMemberStorage(): void {
  clearMemberKeys();
}

/** Clears member storage when the sign-out endpoint marked this browser as signed out. */
export function clearIfSignedOut(): void {
  if (!browser) return;
  const marked = document.cookie.split(";").some((c) => c.trim().startsWith(`${SIGNED_OUT_COOKIE}=`));
  if (!marked) return;
  clearMemberStorage();
  document.cookie = `${SIGNED_OUT_COOKIE}=; path=/; max-age=0; samesite=lax`;
}
