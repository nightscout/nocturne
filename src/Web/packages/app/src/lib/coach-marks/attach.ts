import { page } from "$app/state";
import type { Attachment } from "svelte/attachments";

/**
 * A share viewer or a guest is not toured: the tour's backdrop covers a page
 * they only look at.
 */
export function toured(): boolean {
  return !!page.data.user && !page.data.isGuestSession;
}

type Visibility = (shown: boolean) => void;

/**
 * One callback per element: a second `whileShown` on the same element is not
 * supported.
 */
const watched = new Map<Element, Visibility>();
let observer: ResizeObserver | null = null;

const hasBox = (element: Element) => element.getClientRects().length > 0;

/**
 * One observer for every target, so a layout swap reaches both copies of a
 * target in one callback. The context checks for an unmounted selection in a
 * microtask; with an observer per copy, that check can run between the two
 * callbacks and drop the selection the key still holds.
 */
function observe(element: Element, onChange: Visibility): () => void {
  observer ??= new ResizeObserver((entries) => {
    for (const entry of entries)
      watched.get(entry.target)?.(hasBox(entry.target));
  });
  watched.set(element, onChange);
  observer.observe(element);
  return () => {
    watched.delete(element);
    observer?.unobserve(element);
  };
}

/**
 * Holds `mark` on the element only while the element has a box. A target
 * rendered once per layout, with the other layout hidden, otherwise registers
 * its key twice, and the popover waits on the hidden copy as a step it can
 * never show. A target already shown takes the mark at once, so an attachment
 * that re-runs keeps its mark within the context's re-register window.
 */
export function whileShown(
  mark: Attachment<HTMLElement>
): Attachment<HTMLElement> {
  return (element) => {
    let release: ReturnType<Attachment<HTMLElement>>;
    let held = false;
    const set = (shown: boolean) => {
      if (shown === held) return;
      held = shown;
      if (shown) {
        release = mark(element);
      } else {
        release?.();
        release = undefined;
      }
    };
    set(hasBox(element));
    const unobserve = observe(element, set);
    return () => {
      unobserve();
      set(false);
    };
  };
}
