/** A CSS string literal carrying `text`, safe inside a `<style>` element. */
export function cssString(text: string): string {
  return `"${text.replace(/[\\"<>\n\r]/g, (c) => `\\${c.charCodeAt(0).toString(16)} `)}"`;
}
