import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import {
  findComparedLiterals,
  findTranslatedComparisons,
} from "./comparedLiterals.ts";

test("finds a literal on either side of an equality operator and in a case label", () => {
  const source = [
    'if (a === "One") {}',
    "if ('Two' !== b) {}",
    'if (c == "Three" || d != `Four`) {}',
    'switch (e) { case "Five": break; }',
  ].join("\n");
  assert.deepEqual(
    findComparedLiterals(source)
      .map((c) => [c.literal, c.line])
      .sort(),
    [
      ["Five", 4],
      ["Four", 3],
      ["One", 1],
      ["Three", 3],
      ["Two", 2],
    ],
  );
});

test("finds a literal passed to includes, startsWith or endsWith", () => {
  const source = [
    'if (type.includes("Site")) {}',
    "if (name.startsWith('Pump')) {}",
    "if (name.endsWith(`Change`)) {}",
  ].join("\n");
  assert.deepEqual(
    findComparedLiterals(source)
      .map((c) => [c.literal, c.line])
      .sort(),
    [
      ["Change", 3],
      ["Pump", 2],
      ["Site", 1],
    ],
  );
});

test("unescapes an escaped quote so it matches the catalog text", () => {
  assert.deepEqual(findComparedLiterals('if (a === "A\\"B") {}'), [
    { literal: 'A"B', line: 1 },
  ]);
});

test("ignores a literal that is only assigned, passed or shown", () => {
  const source = [
    'const label = "Shown";',
    'show("Passed");',
    'const x = a ? "Yes" : "No";',
    "if (a >= 1 && b <= 2) {}",
    "const t = `Hello ${name}`;",
  ].join("\n");
  assert.deepEqual(findComparedLiterals(source), []);
});

test("reports only a msgid that the referencing file compares", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "compared-literals-"));
  try {
    fs.mkdirSync(path.join(dir, "src"));
    fs.writeFileSync(
      path.join(dir, "src/Icon.svelte"),
      '{#if state === "EaseOff"}<b>Shown</b>{/if}\n',
    );
    const found = findTranslatedComparisons(
      [
        { msgid: "EaseOff", references: ["src/Icon.svelte"] },
        { msgid: "Shown", references: ["src/Icon.svelte"] },
        { msgid: "EaseOff", references: ["src/Other.svelte"] },
      ],
      dir,
    );
    assert.deepEqual([...found.keys()], ["src/Icon.svelte"]);
    assert.deepEqual(found.get("src/Icon.svelte"), [
      { literal: "EaseOff", line: 1, file: "src/Icon.svelte" },
    ]);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
