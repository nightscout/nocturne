/**
 * Runs `callback` in a task after the next frame, so state set before the call can paint first.
 * A hidden document gets no frames, so there it runs in the next task instead. Returns a cancel.
 */
export function afterNextPaint(callback: () => void): () => void {
  let timeout: ReturnType<typeof setTimeout> | undefined;
  if (
    typeof requestAnimationFrame === "undefined" ||
    typeof document === "undefined" ||
    document.visibilityState === "hidden"
  ) {
    timeout = setTimeout(callback, 0);
    return () => clearTimeout(timeout);
  }
  const frame = requestAnimationFrame(() => {
    timeout = setTimeout(callback, 0);
  });
  return () => {
    cancelAnimationFrame(frame);
    clearTimeout(timeout);
  };
}
