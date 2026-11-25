import { getContext, setContext } from "svelte";
import type { TenantOverviewItem } from "@nocturne/api-client";
import { readingIsNotCurrent } from "./person.js";

export interface ReadState {
  /** The last overview read failed: every reading on screen is one that may have changed since. */
  readonly disconnected: boolean;
  /** The ticking clock the ages are told against, or undefined to read the clock once. */
  readonly now: number | undefined;
}

const KEY = "nocturne:people:read-state";
const DEFAULT: ReadState = { disconnected: false, now: undefined };

export function setReadState(state: ReadState): void {
  setContext(KEY, state);
}

export function getReadState(): ReadState {
  return getContext<ReadState | undefined>(KEY) ?? DEFAULT;
}

/** The one decision whether a reading is drawn as current: the server's status and the read's state. */
export function isShownAsCurrent(person: TenantOverviewItem, read: ReadState): boolean {
  return !readingIsNotCurrent(person) && !read.disconnected;
}
