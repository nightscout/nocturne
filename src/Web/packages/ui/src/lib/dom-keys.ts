/**
 * The values `KeyboardEvent.key` carries. They are fixed DOM names, not copy, so they live in a
 * plain module that wuchale never extracts: a literal in a component is translated, and the
 * handler then never matches in another locale.
 */
export const Key = {
  Enter: "Enter",
  Escape: "Escape",
  Space: " ",
  Tab: "Tab",
  Shift: "Shift",
  Home: "Home",
  End: "End",
  ArrowUp: "ArrowUp",
  ArrowDown: "ArrowDown",
  ArrowLeft: "ArrowLeft",
  ArrowRight: "ArrowRight",
  J: "j",
  K: "k",
  P: "p",
  Y: "y",
  Z: "z",
} as const;
