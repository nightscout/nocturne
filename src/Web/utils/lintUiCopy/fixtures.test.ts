import assert from "node:assert/strict";
import { before, describe, test } from "node:test";
import {
  lintFixtures,
  paragraphs,
  readFixtures,
  ruleNames,
} from "./fixtures.ts";
import type { DocsFinding } from "./docsProse.ts";

/** Concerns that no single rule owns. A fail fixture for one needs any rule to fire. */
const concerns = ["Scopes", "Controls"];

const fixtures = readFixtures();
let findings: DocsFinding[] = [];

before(() => {
  findings = lintFixtures();
});

describe("every rule is pinned by a fixture pair", () => {
  for (const rule of ruleNames()) {
    test(`${rule} has a must-fail and a must-pass fixture`, () => {
      for (const expectation of ["fail", "pass"]) {
        assert.ok(
          fixtures.some(
            (f) => f.subject === rule && f.expectation === expectation,
          ),
          `.vale/fixtures/${rule}.${expectation}.svx is missing`,
        );
      }
    });
  }

  test("a fixture is named for a rule or a concern", () => {
    const known = new Set([...ruleNames(), ...concerns]);
    for (const f of fixtures) {
      assert.ok(known.has(f.subject), `${f.file} names no rule or concern`);
    }
  });
});

describe("a must-fail fixture raises its rule on every sentence", () => {
  for (const fixture of fixtures.filter((f) => f.expectation === "fail")) {
    test(fixture.file, () => {
      const sentences = paragraphs(fixture.source);
      assert.notEqual(sentences.length, 0, "the fixture holds no sentence");
      const own = findings.filter((f) => f.file === fixture.file);
      const missed = sentences.filter(
        (p) =>
          !own.some(
            (f) =>
              f.line >= p.line &&
              f.line <= p.endLine &&
              (concerns.includes(fixture.subject) ||
                f.rule === `Nocturne.${fixture.subject}`),
          ),
      );
      assert.deepEqual(
        missed.map((p) => `line ${p.line}: ${p.text}`),
        [],
      );
    });
  }
});

describe("a must-pass fixture raises no rule", () => {
  for (const fixture of fixtures.filter((f) => f.expectation === "pass")) {
    test(fixture.file, () => {
      assert.notEqual(paragraphs(fixture.source).length, 0);
      assert.deepEqual(
        findings
          .filter((f) => f.file === fixture.file)
          .map((f) => `line ${f.line}: ${f.rule} "${f.match}"`),
        [],
      );
    });
  }
});
