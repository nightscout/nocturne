const DELIMITERS = new Set(["\\", '"', "<", ">"]);

/**
 * A CSS string literal carrying `text`, safe inside a `<style>` element.
 * Every control character is escaped. CSS reads a form feed as a line break,
 * which ends the string and lets the rest of `text` parse as rules.
 */
export function cssString(text: string): string {
  let body = "";
  for (const c of text) {
    const code = c.codePointAt(0) ?? 0;
    body += code < 0x20 || code === 0x7f || DELIMITERS.has(c) ? `\\${code.toString(16)} ` : c;
  }
  return `"${body}"`;
}
