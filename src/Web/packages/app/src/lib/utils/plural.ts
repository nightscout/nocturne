import { formatLocale } from "./formatting";

/**
 * The plural form of a message for `n`, with `#` standing for the number.
 *
 * wuchale recognises a call named `plural` with a literal array of forms: it extracts them as one
 * plural message, swaps the array for the current locale's forms and passes that locale's rule as
 * `rule`. So the forms must be written inline at the call, in English singular-then-plural order,
 * never built or passed through a variable. Without wuchale (tests), the English rule applies.
 */
export function plural(
  n: number,
  forms: string[],
  rule: (n: number) => number = (count) => (count === 1 ? 0 : 1)
): string {
  const form = forms[rule(n)] ?? forms[forms.length - 1] ?? "";
  return form.replace("#", n.toLocaleString(formatLocale()));
}
