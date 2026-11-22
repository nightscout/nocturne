/** How long the controls over a fullscreen surface stay after the last touch or pointer move. */
export const OVERLAY_REVEAL_MS = 4000;

export interface OverlayReveal {
  readonly revealed: boolean;
  /** Set while the last pointer was a touch and the controls are hidden: they take no taps. */
  readonly inert: boolean;
  pointerdown(event: PointerEvent): void;
  pointermove(event: PointerEvent): void;
  keydown(): void;
  clickCapture(event: MouseEvent): void;
}

/**
 * The reveal state of the controls over a fullscreen surface. Touch has no hover, so a tap shows
 * them for a few seconds. A mouse reveals them by hovering them, and by moving anywhere when
 * `revealOnPointerMove` is set. A key press hands them back to the keyboard.
 *
 * Call during component initialization: the timer is owned by an `$effect`.
 */
export function createOverlayReveal(
  options: { revealOnPointerMove?: boolean } = {}
): OverlayReveal {
  let revealed = $state(false);
  let touchInput = $state(false);
  let hideTimer: ReturnType<typeof setTimeout> | undefined;
  // A screen whose primary pointer is fine still lets a tap land on the hidden controls: the tap
  // that reveals them must not also press the button under it.
  let tapRevealed = false;

  function reveal() {
    revealed = true;
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => (revealed = false), OVERLAY_REVEAL_MS);
  }

  $effect(() => () => clearTimeout(hideTimer));

  return {
    get revealed() {
      return revealed;
    },
    get inert() {
      return touchInput && !revealed;
    },
    pointerdown(event) {
      touchInput = event.pointerType !== "mouse";
      tapRevealed = touchInput && !revealed;
      if (touchInput) reveal();
    },
    pointermove(event) {
      if (event.pointerType !== "mouse") return;
      touchInput = false;
      if (options.revealOnPointerMove) reveal();
    },
    keydown() {
      touchInput = false;
      tapRevealed = false;
    },
    clickCapture(event) {
      if (!tapRevealed) return;
      tapRevealed = false;
      event.preventDefault();
      event.stopPropagation();
    },
  };
}
