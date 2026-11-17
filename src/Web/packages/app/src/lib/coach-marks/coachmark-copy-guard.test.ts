import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

/**
 * Copy in an inline `{@attach}` is not extracted, so it lives in functions in
 * `copy.svelte.ts` and a call site refers to it by key.
 *
 * It reads text: a call is the balanced parentheses after `coachmark(`, and a
 * string is a quote or backtick following a `title`, `description` or `label`
 * property, or sitting inside a `steps` array. Copy reached through a variable,
 * or a literal built by concatenation behind a call, is not seen.
 */
const SRC = fileURLToPath(new URL("../..", import.meta.url));

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = `${dir}/${entry.name}`;
    if (entry.isDirectory())
      return entry.name === "generated" ? [] : sourceFiles(path);
    return /\.(svelte|ts)$/.test(entry.name) &&
      !/\.test\.(svelte|ts)$/.test(entry.name)
      ? [path]
      : [];
  });
}

function callBodies(text: string): string[] {
  const bodies: string[] = [];
  for (const match of text.matchAll(/\bcoachmark\(/g)) {
    let depth = 1;
    let i = match.index + match[0].length;
    const start = i;
    while (i < text.length && depth > 0) {
      if (text[i] === "(") depth++;
      else if (text[i] === ")") depth--;
      i++;
    }
    bodies.push(text.slice(start, i - 1));
  }
  return bodies;
}

const INLINE_COPY =
  /\b(?:title|description|label)\s*:\s*["'`]|\bsteps\s*:\s*\[\s*[{"'`]/;

function inlineCopyIn(text: string): string[] {
  return callBodies(text).filter((body) => INLINE_COPY.test(body));
}

describe("coachmark copy", () => {
  it("is not written inline at a call site", () => {
    const offenders = sourceFiles(SRC).filter(
      (file) => inlineCopyIn(readFileSync(file, "utf8")).length > 0
    );
    expect(offenders.map((file) => file.slice(SRC.length))).toEqual([]);
  });

  it("recognises each inline shape", () => {
    expect(
      inlineCopyIn(`coachmark({ key: "a", title: "Hi", description: "There" })`)
    ).toHaveLength(1);
    expect(
      inlineCopyIn("coachmark({ key: 'a', description: `Hi ${x}` })")
    ).toHaveLength(1);
    expect(
      inlineCopyIn(
        `coachmark({ key: "a", steps: [{ title: t, description: d }] })`
      )
    ).toHaveLength(1);
    expect(
      inlineCopyIn(
        `coachmark({ key: "a", action: { label: "Go", href: "/x" } })`
      )
    ).toHaveLength(1);
  });

  it("allows copy taken by key", () => {
    expect(
      inlineCopyIn(
        `coachmark({ ...coachFor("a"), completeOn: { event: "click" } })`
      )
    ).toEqual([]);
    expect(
      inlineCopyIn(`coachmark({ key: "a", ...inviteLinkCopy(expiryLabel) })`)
    ).toEqual([]);
  });
});
