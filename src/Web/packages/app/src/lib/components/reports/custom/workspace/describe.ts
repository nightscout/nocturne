/**
 * `aria-description` as attributes to spread: Svelte's element types do not
 * list it yet.
 */
export function describe(
  text: string | null | undefined
): Record<string, string> {
  return text ? { "aria-description": text } : {};
}
