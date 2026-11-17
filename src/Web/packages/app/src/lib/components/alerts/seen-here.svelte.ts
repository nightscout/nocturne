import { SvelteSet } from "svelte/reactivity";
import type { ActiveExcursionResponse } from "#lib/api/generated/nocturne-api-client.js";

const STORAGE_KEY = "nocturne:alerts-seen-here";
/** Excursion ids are unique, so the oldest can go once an alarm is long closed. */
const KEPT = 50;

function load(): string[] {
  if (typeof localStorage === "undefined") return [];
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter((id) => typeof id === "string") : [];
  } catch (err) {
    if (err instanceof SyntaxError) return [];
    throw err;
  }
}

/**
 * Alarms someone pressed Seen on from this device. A public link is one viewer
 * to the server however many screens open it, so its receipt cannot say which
 * screen saw the alarm; this does. Kept in localStorage so a tablet that
 * reloads does not show the alarm again.
 */
const seenOnThisDevice = new SvelteSet<string>(load());

export function markSeenOnThisDevice(id: string): void {
  seenOnThisDevice.add(id);
  if (typeof localStorage === "undefined") return;
  localStorage.setItem(STORAGE_KEY, JSON.stringify([...seenOnThisDevice].slice(-KEPT)));
}

/**
 * Whether a viewer without an account has already seen this alarm here. A
 * guest link is one device, so its own receipt also counts; a public link's
 * receipt may come from another screen, so only this device's press does.
 */
export function seenHere(
  alert: ActiveExcursionResponse,
  viewer: { hasAccount: boolean; isShareHost: boolean }
): boolean {
  if (viewer.hasAccount) return false;
  if (seenOnThisDevice.has(alert.id ?? "")) return true;
  return !viewer.isShareHost && !!alert.receipts?.length;
}
