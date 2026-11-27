import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { frontmatterEnd, lintDocsProse, type DocsFinding } from "./docsProse.ts";

export const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../../..",
);
export const stylesDir = path.join(repoRoot, ".vale/styles/Nocturne");
export const fixturesDir = path.join(repoRoot, ".vale/fixtures");

export type Expectation = "fail" | "pass";

export interface Fixture {
  /** A rule name, or the name of a concern that no single rule owns. */
  subject: string;
  expectation: Expectation;
  /** Repo-relative path, as `lintDocsProse` reports it. */
  file: string;
  source: string;
}

export interface Paragraph {
  line: number;
  endLine: number;
  text: string;
}

export const withPage = <T>(source: string, run: (dir: string) => T): T => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "docs-prose-"));
  try {
    fs.writeFileSync(path.join(dir, "page.svx"), source);
    return run(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
};

export const lineOf = (source: string, text: string): number => {
  const index = source.split("\n").findIndex((l) => l.includes(text));
  assert.notEqual(index, -1, `fixture has no line with ${text}`);
  return index + 1;
};

export const ruleNames = (): string[] =>
  fs
    .readdirSync(stylesDir)
    .filter((f) => f.endsWith(".yml"))
    .map((f) => path.basename(f, ".yml"))
    .sort();

export const readFixtures = (): Fixture[] =>
  fs
    .readdirSync(fixturesDir)
    .flatMap((name) => {
      const match = /^(\w+)\.(fail|pass)\.svx$/.exec(name);
      if (!match) return [];
      const full = path.join(fixturesDir, name);
      return [
        {
          subject: match[1],
          expectation: match[2] as Expectation,
          file: path.relative(repoRoot, full),
          source: fs.readFileSync(full, "utf8"),
        },
      ];
    })
    .sort((a, b) => a.file.localeCompare(b.file));

const isComment = (line: string): boolean => /^\s*<!--[\s\S]*?-->\s*$/.test(line);

/**
 * The blank-line separated blocks of a fixture below its frontmatter. A block
 * made only of HTML comments is a Vale control, not a sentence, so it is left
 * out.
 */
export function paragraphs(source: string): Paragraph[] {
  const lines = source.split(/\r?\n/);
  const result: Paragraph[] = [];
  let start = frontmatterEnd(lines);
  const flush = (end: number) => {
    const block = lines.slice(start, end);
    if (block.length > 0 && !block.every(isComment)) {
      result.push({
        line: start + 1,
        endLine: end,
        text: block.join("\n"),
      });
    }
  };
  for (let i = start; i <= lines.length; i++) {
    if (i === lines.length || lines[i].trim() === "") {
      flush(i);
      start = i + 1;
    }
  }
  return result;
}

/** Every alert Vale raises over the fixture pages, in one Vale run. */
export function lintFixtures(): DocsFinding[] {
  const { findings, error } = lintDocsProse(repoRoot, fixturesDir, "suggestion");
  if (error) throw new Error(error);
  return findings;
}
