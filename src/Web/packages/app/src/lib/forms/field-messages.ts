/**
 * Validation messages accepted by {@link FormField} and {@link FormError}.
 *
 * Covers every shape the app already produces: SvelteKit's
 * `form.fields.x.issues()`, {@link FormGuard.issuesFor}'s Zod issues, a plain
 * string, or a list of strings.
 */
export type FieldIssues =
  | readonly { message: string }[]
  | readonly string[]
  | string
  | null
  | undefined;

/** Normalises {@link FieldIssues} into a list of message strings. */
export function fieldMessages(issues: FieldIssues): string[] {
  if (issues == null) return [];
  if (typeof issues === "string") {
    return issues.trim() === "" ? [] : [issues];
  }

  const messages: string[] = [];
  for (const issue of issues) {
    const message = typeof issue === "string" ? issue : issue?.message;
    if (typeof message === "string" && message.trim() !== "") {
      messages.push(message);
    }
  }
  return messages;
}
