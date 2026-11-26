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

const contractionsIn = (prose: string): string[] => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "contractions-"));
  try {
    fs.writeFileSync(path.join(dir, "page.svx"), `---\ntitle: T\n---\n\n${prose}\n`);
    const { findings, error } = lintDocsProse(repoRoot, dir, "error");
    assert.equal(error, undefined);
    return findings
      .filter((f) => f.rule === "Nocturne.Contractions")
      .map((f) => f.match);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
};

test("an 's contraction of a word with no possessive is flagged", () => {
  for (const word of [
    "there",
    "that",
    "what",
    "here",
    "who",
    "where",
    "he",
    "she",
  ]) {
    assert.deepEqual(contractionsIn(`Say ${word}'s now.`), [`${word}'s`]);
  }
});

test("a curly apostrophe and a capital are flagged", () => {
  assert.deepEqual(contractionsIn("What’s next?"), ["What’s"]);
  assert.deepEqual(contractionsIn("There's no list."), ["There's"]);
  assert.deepEqual(contractionsIn("That’s the point."), [
    "That’s",
  ]);
});

test("a possessive is not flagged", () => {
  assert.deepEqual(
    contractionsIn("Ask the owner's team. Use Nocturne's site. Read the user’s name. Clear the cache's size."),
    [],
  );
});
