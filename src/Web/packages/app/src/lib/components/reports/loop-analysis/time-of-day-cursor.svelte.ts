import { SLOTS_PER_DAY } from "./daily-profile-view";

/**
 * The state a stack of time-of-day panels shares: the hovered slot, the slot picked for the day
 * list, and the one panel that holds the stack's single tab stop.
 */
export class TimeOfDayCursor {
  slot = $state<number | null>(null);
  /** The panel under the pointer or focus, which alone shows the tooltip; the others show only the crosshair. */
  panel = $state<string | null>(null);
  selected = $state<number | null>(null);
  /** The slot was picked from the keyboard, so the day list takes focus when it opens. */
  selectedByKeyboard = $state(false);
  /** The panel that is in the tab order; the others are reached with the up and down arrows. */
  tabStop = $state<string | null>(null);

  #panels: { id: string; element: HTMLElement }[] = [];

  /** How many slots the stack's axis has: a day's by default. */
  readonly slotCount: number;

  constructor(slotCount = SLOTS_PER_DAY) {
    this.slotCount = slotCount;
  }

  /** Adds a panel in document order. Returns the call that removes it. */
  register(id: string, element: HTMLElement): () => void {
    this.#panels = [...this.#panels.filter((p) => p.id !== id), { id, element }].sort((a, b) =>
      a.element.compareDocumentPosition(b.element) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1
    );
    if (this.tabStop == null || !this.#panels.some((p) => p.id === this.tabStop))
      this.tabStop = this.#panels[0]?.id ?? null;
    return () => {
      this.#panels = this.#panels.filter((p) => p.id !== id);
      if (this.tabStop === id) this.tabStop = this.#panels[0]?.id ?? null;
    };
  }

  hover(panel: string, slot: number) {
    this.panel = panel;
    this.slot = Math.min(this.slotCount - 1, Math.max(0, slot));
  }

  leave(panel: string) {
    if (this.panel !== panel) return;
    this.panel = null;
    this.slot = null;
  }

  select(slot: number, byKeyboard = false) {
    this.selected = this.selected === slot ? null : slot;
    this.selectedByKeyboard = byKeyboard;
  }

  /** Moves focus, and the tab stop, to the panel `by` places from `from`, keeping the slot. */
  focusNeighbour(from: string, by: number) {
    const index = this.#panels.findIndex((p) => p.id === from);
    const next = this.#panels[index + by];
    if (index < 0 || !next) return;
    const slot = this.slot;
    this.tabStop = next.id;
    next.element.focus();
    if (slot != null) this.hover(next.id, slot);
  }

  /** Puts focus back on the panel holding the tab stop, as when the day list closes. */
  restoreFocus() {
    this.#panels.find((p) => p.id === this.tabStop)?.element.focus();
  }
}
