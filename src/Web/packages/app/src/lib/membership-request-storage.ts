import type { ReadonlyURL } from "$app/state";
import { withoutQueryParam, withQueryParam } from "#lib/navigation/query-param.js";

/**
 * A membership-request message is parked while the visitor goes off to sign in, then submitted
 * once they come back authenticated. The draft belongs to the one sign-in flow that parked it: its
 * key carries a one-time flow id that rides the return URL, so a later sign-in in the same browser,
 * without that id, never submits someone else's introduction. It lives in sessionStorage so it
 * dies with the tab, and expires on its own after {@link DRAFT_TTL_MS}.
 *
 * The key is namespaced by host rather than by tenant slug. The request is submitted to whichever
 * tenant the host resolves to, and the apex and share hosts have no slug of their own to name.
 */
const STORAGE_KEY_PREFIX = "nocturne:membership-request:";

export const MEMBERSHIP_REQUEST_FLOW_PARAM = "membershipRequest";

export const DRAFT_TTL_MS = 30 * 60 * 1000;

type DraftStorage = Pick<Storage, "getItem" | "setItem" | "removeItem" | "key" | "length">;

interface ParkedDraft {
  message: string;
  parkedAt: number;
}

function hostPrefix(host: string): string {
  return `${STORAGE_KEY_PREFIX}${host}:`;
}

/**
 * Parks `message` for the flow `flowId` and returns `returnPath` carrying that id. When the draft
 * cannot be stored the path comes back unmarked, so the sign-in carries no flow to finish.
 */
export function parkMembershipRequest(
  storage: DraftStorage,
  host: string,
  message: string,
  returnPath: string,
  flowId: string,
  now: number,
): string {
  const draft: ParkedDraft = { message, parkedAt: now };
  try {
    storage.setItem(`${hostPrefix(host)}${flowId}`, JSON.stringify(draft));
  } catch {
    return returnPath;
  }
  return withQueryParam(returnPath, MEMBERSHIP_REQUEST_FLOW_PARAM, flowId);
}

export interface TakenMembershipRequest {
  /** The message to submit, or null when this arrival finishes no live flow. */
  message: string | null;
  /** `url` without the flow id, or null when it carried none. */
  rest: URL | null;
}

/**
 * Removes and returns the draft the flow id on `url` names, if it is still live. Every expired or
 * unreadable draft on `host` is discarded unsent on the way, as is the draft an older build left
 * under the bare host key in `legacy` storage, which no flow owns.
 */
export function takeMembershipRequest(
  storage: DraftStorage,
  legacy: Pick<Storage, "removeItem">,
  host: string,
  url: ReadonlyURL,
  now: number,
): TakenMembershipRequest {
  legacy.removeItem(`${STORAGE_KEY_PREFIX}${host}`);

  const prefix = hostPrefix(host);
  const flowId = url.searchParams.get(MEMBERSHIP_REQUEST_FLOW_PARAM);
  const flowKey = flowId ? `${prefix}${flowId}` : null;

  let message: string | null = null;
  const keys: string[] = [];
  for (let i = 0; i < storage.length; i++) {
    const key = storage.key(i);
    if (key?.startsWith(prefix)) keys.push(key);
  }
  for (const key of keys) {
    const draft = readDraft(storage.getItem(key));
    const live = draft !== null && now - draft.parkedAt <= DRAFT_TTL_MS;
    if (key === flowKey) {
      if (live) message = draft.message;
      storage.removeItem(key);
    } else if (!live) {
      storage.removeItem(key);
    }
  }

  return { message, rest: withoutQueryParam(url, MEMBERSHIP_REQUEST_FLOW_PARAM) };
}

function readDraft(raw: string | null): ParkedDraft | null {
  if (raw === null) return null;
  try {
    const value: unknown = JSON.parse(raw);
    if (
      typeof value === "object" &&
      value !== null &&
      "message" in value &&
      typeof value.message === "string" &&
      "parkedAt" in value &&
      typeof value.parkedAt === "number"
    ) {
      return { message: value.message, parkedAt: value.parkedAt };
    }
  } catch {
    return null;
  }
  return null;
}
