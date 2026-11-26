import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { test } from "node:test";
import {
  fixturesDir,
  lineOf,
  readFixtures,
  repoRoot,
  withPage,
} from "./fixtures.ts";
import {
  controlProblems,
  findControlProblems,
  type ControlProblem,
} from "./inlineControls.ts";

const linesWith = (source: string, problem: ControlProblem): number[] =>
  controlProblems(source)
    .filter((p) => p.problem === problem)
    .map((p) => p.line);

const readFixture = (...name: string[]) =>
  fs.readFileSync(path.join(fixturesDir, ...name), "utf8");

const page = (...lines: string[]) => lines.join("\n");

test("a control with no reason comment beside it is reported at its line", () => {
  const source = readFixture("controls", "unreasoned.svx");
  assert.deepEqual(linesWith(source, "no reason"), [
    lineOf(source, "vale off"),
    lineOf(source, "Nocturne.Click = NO"),
    lineOf(source, "Nocturne.Terms = NO"),
    lineOf(source, "Nocturne.Banned = no"),
  ]);
  assert.deepEqual(linesWith(source, "never restarted"), []);
  assert.deepEqual(linesWith(source, "not on its own line"), []);
});

test("a control with a reason comment on the line before or after it passes", () => {
  assert.deepEqual(controlProblems(readFixture("Controls.pass.svx")), []);
});

test("every control in the must-pass fixtures is alone, has a reason and is started again", () => {
  for (const f of readFixtures().filter((x) => x.expectation === "pass")) {
    assert.deepEqual(controlProblems(f.source), [], f.file);
  }
});

test("a reason comment two lines away, or in a control, is no reason", () => {
  assert.deepEqual(
    linesWith(
      page(
        "<!-- Quotes the Dexcom menu label. -->",
        "",
        "<!-- vale off -->",
        "<!-- vale Nocturne.Click = NO -->",
        "<!-- vale Nocturne.Click = YES -->",
        "<!-- vale on -->",
      ),
      "no reason",
    ),
    [3, 4],
  );
});

test("a control that starts linting again needs no reason", () => {
  assert.deepEqual(
    controlProblems(
      page("<!-- vale on -->", "<!-- vale Nocturne.Click = YES -->"),
    ),
    [],
  );
});

test("a control that is never started again is reported at its line", () => {
  const source = readFixture("controls", "unclosed.svx");
  assert.deepEqual(linesWith(source, "never restarted"), [
    lineOf(source, "<!-- vale off -->"),
    lineOf(source, "Nocturne.Terms = NO"),
    lineOf(source, "Nocturne.Filler = NO"),
    lineOf(source, "Nocturne.Slop = NO"),
    lineOf(source, "Nocturne.Tone = NO"),
  ]);
  assert.deepEqual(linesWith(source, "no reason"), []);
  assert.deepEqual(linesWith(source, "not on its own line"), []);
});

test("only the form Vale honours starts linting again", () => {
  const stop = "<!-- vale Nocturne.Click = NO -->";
  const reason = "<!-- Quotes a Dexcom menu label. -->";
  const restarts = (closer: string) =>
    linesWith(page(stop, reason, "", closer), "never restarted").length === 0;

  for (const closer of [
    "<!-- vale Nocturne.Click = YES -->",
    "<!--vale Nocturne.Click = YES-->",
    "<!--   vale Nocturne.Click = YES   -->",
  ]) {
    assert.ok(restarts(closer), closer);
  }
  for (const closer of [
    "<!-- vale nocturne.click = YES -->",
    "<!-- vale Nocturne.Click = Yes -->",
    "<!-- vale Nocturne.Click=YES -->",
    "<!-- vale Nocturne.Click =  YES -->",
    "<!-- vale Nocturne.Terms = YES -->",
    "<!-- vale on -->",
  ]) {
    assert.ok(!restarts(closer), closer);
  }

  const off = ["<!-- vale off -->", reason, ""];
  const restartsAll = (closer: string) =>
    linesWith(page(...off, closer), "never restarted").length === 0;
  assert.ok(restartsAll("<!-- vale on -->"));
  assert.ok(restartsAll("<!--vale on-->"));
  assert.ok(!restartsAll("<!-- vale ON -->"));
  assert.ok(!restartsAll("<!--   vale   on   -->"));
  assert.ok(!restartsAll("<!-- vale Nocturne.Click = YES -->"));
});

test("a control that starts linting again must come after the control that stops it", () => {
  assert.deepEqual(
    linesWith(
      page(
        "<!-- vale Nocturne.Banned = YES -->",
        "<!-- vale Nocturne.Banned = NO -->",
        "<!-- Quotes a Dexcom menu label. -->",
      ),
      "never restarted",
    ),
    [2],
  );
});

test("a control inside a paragraph or split over lines is reported as not on its own line", () => {
  const inline = readFixture("controls", "inline.svx");
  assert.deepEqual(controlProblems(inline), [
    {
      line: lineOf(inline, "Quoted text <!--"),
      text: "<!-- vale off -->",
      problem: "not on its own line",
    },
  ]);

  const split = readFixture("controls", "split.svx");
  assert.deepEqual(controlProblems(split), [
    {
      line: lineOf(split, "<!--"),
      text: "<!-- vale Nocturne.Terms = NO -->",
      problem: "not on its own line",
    },
  ]);
});

test("a docs page is checked, and reported with its file and line", () => {
  const found = withPage("Text.\n\n<!-- vale off -->\n", (dir) =>
    findControlProblems(repoRoot, dir),
  );
  assert.deepEqual(
    found.map((f) => [f.line, f.text, f.problem]),
    [
      [3, "<!-- vale off -->", "no reason"],
      [3, "<!-- vale off -->", "never restarted"],
    ],
  );
  assert.match(found[0].file, /page\.svx$/);

  const fine = withPage(
    page(
      "<!-- vale off -->",
      "<!-- Quotes a command here. -->",
      "",
      "Text.",
      "",
      "<!-- vale on -->",
      "",
    ),
    (dir) => findControlProblems(repoRoot, dir),
  );
  assert.deepEqual(fine, []);
});
