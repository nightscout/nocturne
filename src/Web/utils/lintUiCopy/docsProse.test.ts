import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { lintDocsProse } from "./docsProse.ts";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../../..",
);

const withPage = <T>(source: string, run: (dir: string) => T): T => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "docs-prose-"));
  try {
    fs.writeFileSync(path.join(dir, "page.svx"), source);
    return run(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
};

const lineOf = (source: string, text: string): number => {
  const index = source.split("\n").findIndex((l) => l.includes(text));
  assert.notEqual(index, -1, `fixture has no line with ${text}`);
  return index + 1;
};

const page = [
  "---",
  "title: Click the title",
  "description: Click the description",
  "---",
  "",
  "<script>",
  "  const label = 'Click here';",
  "</script>",
  "",
  "import Click from './Click.svelte';",
  "",
  '<Callout type="warning" title="Click the title">',
  "",
  "  Click this prose in a component body.",
  "",
  "</Callout>",
  "",
  "<Screenshot",
  '  id="one"',
  `  alt="Click the colour; you'll see it"`,
  "/>",
  "",
  "```",
  "Click in a code fence",
  "```",
  "",
  "Use `Click` in inline code.",
  "",
  "Click this prose in a paragraph.",
  "",
].join("\n");

test("lints the prose of a docs page at its own line numbers and nothing else", () => {
  const { findings, error } = withPage(page, (dir) =>
    lintDocsProse(repoRoot, dir, "error"),
  );
  assert.equal(error, undefined);
  assert.deepEqual(
    findings.map((f) => f.line).sort((a, b) => a - b),
    [
      lineOf(page, "Click this prose in a component body"),
      lineOf(page, "Click this prose in a paragraph"),
    ],
  );
});

const tagged = [
  '<Callout type="warning" title="NOT over missing data is true">',
  "",
  "  Most conditions are simply not true when the data they need is missing. NOT",
  '  turns that into true. "NOT pump suspended" is true on a site where the pump',
  '  has never reported anything, and "NOT glucose below 70" is true while there is',
  "  no reading at all. Check what NOT will do when data is missing before relying",
  "  on it.",
  "</Callout>",
  "",
].join("\n");

test("a component tag does not join the sentence that follows it", () => {
  const { findings } = withPage(tagged, (dir) =>
    lintDocsProse(repoRoot, dir, "warning"),
  );
  const lengths = findings
    .filter((f) => f.rule === "Nocturne.SentenceLength")
    .map((f) => f.line);
  assert.deepEqual(lengths, [lineOf(tagged, "Most conditions are simply")]);
});

test("a Vale that is missing is reported as an error with no findings", () => {
  const result = withPage(page, (dir) =>
    lintDocsProse(repoRoot, dir, "error", "vale-that-is-not-installed"),
  );
  assert.deepEqual(result.findings, []);
  assert.match(result.error ?? "", /not installed/);
});

test("a Vale that fails is reported as an error with no findings", () => {
  const noConfig = fs.mkdtempSync(path.join(os.tmpdir(), "docs-prose-root-"));
  try {
    const result = withPage(page, (dir) =>
      lintDocsProse(noConfig, dir, "error"),
    );
    assert.deepEqual(result.findings, []);
    assert.ok(result.error, "the failure message is returned");
  } finally {
    fs.rmSync(noConfig, { recursive: true, force: true });
  }
});

test("a directory that holds no docs page is reported as an error", () => {
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), "docs-prose-empty-"));
  try {
    const none = lintDocsProse(repoRoot, empty, "error");
    assert.deepEqual(none.findings, []);
    assert.match(none.error ?? "", /No \.svx page/);

    const moved = lintDocsProse(repoRoot, path.join(empty, "moved"), "error");
    assert.match(moved.error ?? "", /No \.svx page/);
  } finally {
    fs.rmSync(empty, { recursive: true, force: true });
  }
});
