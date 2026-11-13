import { tick } from "svelte";

/** Where the focus goes once something is removed, asked after the removal. */
export type FocusTarget = () => Element | null | undefined;

/**
 * Removes something the focus is on, then moves the focus to `target`: the item before it, else the
 * control that adds one. Without this the focus falls back to the page when its element goes. Waits
 * a frame too, so a closing popover's own return of the focus comes first.
 */
export async function removeThenFocus(target: FocusTarget, remove: () => unknown): Promise<void> {
  remove();
  await tick();
  await new Promise((resolve) => requestAnimationFrame(resolve));
  const element = target();
  if (element instanceof HTMLElement && element.isConnected) element.focus();
}

/**
 * The element before `item` among `items`, read now, while `item` is still there; else, after the
 * removal, `fallback`'s answer, which may only then exist.
 */
export function before(items: Element[], item: Element | null, fallback: FocusTarget): FocusTarget {
  const at = item ? items.indexOf(item) : -1;
  const previous = at > 0 ? items[at - 1] : null;
  return () => previous ?? fallback();
}
