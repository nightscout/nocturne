import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { ONBOARDING_CORE_GATE, sequences } from "./sequences";

const SRC = fileURLToPath(new URL("../..", import.meta.url));

/** Keys attached with no sequence around them. Each one is eligible on every page load. */
const STANDALONE_KEYS: readonly string[] = [];

/** Its `coachmark` call attaches the key passed to `loopAnalysisMark`, so the scan reads those calls. */
const KEY_FORWARDER = join("lib", "components", "reports", "loop-analysis", "loop-analysis-tour.svelte.ts");

interface Attachment {
  file: string;
  /** The literal key of each attachment in the call, by `key:` or `coachFor`; null for one that is not a literal. */
  keys: (string | null)[];
}

/** The text of the call starting at `open` (a `(`), skipping over parens inside string literals. */
function callBody(source: string, open: number): string {
  let depth = 0;
  let quote: string | null = null;
  for (let i = open; i < source.length; i++) {
    const c = source[i];
    if (quote) {
      if (c === "\\") i++;
      else if (c === quote) quote = null;
    } else if (c === '"' || c === "'" || c === "`") quote = c;
    else if (c === "(") depth++;
    else if (c === ")" && --depth === 0) return source.slice(open + 1, i);
  }
  throw new Error("unterminated coachmark( call");
}

export function findAttachments(source: string, file: string): Attachment[] {
  const attachments: Attachment[] = [];
  for (const match of source.matchAll(/\b(coachmark|loopAnalysisMark)\(/g)) {
    const body = callBody(source, match.index + match[0].length - 1);
    const keyAt = match[1] === "coachmark" ? /\b(?:key:|coachFor\()\s*/ : /^\s*/;
    const literal = /(?:"([^"]*)"|'([^']*)'|(\S))/;
    const keys = [...body.matchAll(new RegExp(keyAt.source + literal.source, "g"))].map(
      ([, double, single]) => double ?? single ?? null,
    );
    // The forwarder's own coachmark call, its signature and its doc comment carry the key by name.
    if (file === KEY_FORWARDER && keys.every((key) => key === null)) continue;
    attachments.push({ file, keys: keys.length > 0 ? keys : [null] });
  }
  return attachments;
}

function scanApp(): Attachment[] {
  return readdirSync(SRC, { recursive: true, encoding: "utf8" })
    .filter((path) => /\.(svelte|ts)$/.test(path) && !/\.test\.|[\\/]generated[\\/]/.test(path))
    .flatMap((path) => findAttachments(readFileSync(join(SRC, path), "utf8"), relative(SRC, join(SRC, path))));
}

const attachments = scanApp();
const attachedKeys = new Set(attachments.flatMap((a) => a.keys));
const sequenceSteps = Object.values(sequences).flatMap((seq) => seq.steps);

describe("coach mark keys and their attachment sites", () => {
  it("finds the attachment sites it guards", () => {
    expect(attachments.length).toBeGreaterThan(0);
    expect(sequenceSteps.length).toBeGreaterThan(0);
  });

  it("reads a key from every attachment", () => {
    const unreadable = attachments.filter((a) => a.keys.includes(null)).map((a) => a.file);
    expect(unreadable, "coachmark() calls without a string-literal key").toEqual([]);
  });

  it("attaches every step of every sequence somewhere in the app", () => {
    const unattached = sequenceSteps.filter((key) => !attachedKeys.has(key));
    expect(unattached).toEqual([]);
  });

  it("attaches no key outside a sequence unless it is declared standalone", () => {
    const steps = new Set(sequenceSteps);
    const stray = [...attachedKeys].filter(
      (key) => key !== null && !steps.has(key) && !STANDALONE_KEYS.includes(key),
    );
    expect(stray).toEqual([]);
  });

  it("attaches the whole quick tour on the dashboard, the one page it runs on", () => {
    const dashboard = new Set(
      attachments
        .filter((a) => a.file === join("routes", "(authenticated)", "+page.svelte"))
        .flatMap((a) => a.keys),
    );
    expect(sequences["quick-tour"].steps.filter((key) => !dashboard.has(key))).toEqual([]);
  });
});

describe("the attachment scan", () => {
  it("reports a sequence key nothing attaches", () => {
    const found = findAttachments(
      `<div {@attach coachmark({ key: "tour.one", title: "a (b" })}></div>
       <div {@attach coachmark([{ key: 'tour.two', title: "x" }, { key: "tour.three" }])}></div>`,
      "fixture.svelte",
    ).flatMap((a) => a.keys);

    expect(found).toEqual(["tour.one", "tour.two", "tour.three"]);
    expect(["tour.one", "tour.four"].filter((key) => !found.includes(key))).toEqual(["tour.four"]);
  });

  it("reads a key taken from the copy table or passed to the Loop Analysis helper", () => {
    const found = findAttachments(
      `<div {@attach coachmark({ ...coachFor("tour.one"), completeOn: { event: "click" } })}></div>
       <div {@attach loopAnalysisMark("tour.two")}></div>`,
      "fixture.svelte",
    ).flatMap((a) => a.keys);

    expect(found).toEqual(["tour.one", "tour.two"]);
  });

  it("flags a key it cannot read", () => {
    const found = findAttachments(`coachmark({ key: someKey, title: "t" })`, "fixture.ts");
    expect(found[0].keys).toEqual([null]);
  });
});

describe("coach mark sequences", () => {
  it("retires the first-run setup sequences the setup hub replaced", () => {
    for (const retired of [
      "onboarding",
      "setup-invite",
      "setup-alerts",
      "setup-reports",
      "setup-connectors",
    ]) {
      expect(sequences, retired).not.toHaveProperty(retired);
    }
  });

  it("gates a tour on the onboarding core, never on a sequence", () => {
    for (const [name, seq] of Object.entries(sequences)) {
      if (seq.prerequisite !== undefined) expect(seq.prerequisite, name).toBe(ONBOARDING_CORE_GATE);
    }
    expect(sequences["quick-tour"].prerequisite).toBe(ONBOARDING_CORE_GATE);
    expect(sequences["feature-intro"].prerequisite).toBe(ONBOARDING_CORE_GATE);
    expect(sequences).not.toHaveProperty(ONBOARDING_CORE_GATE);
  });

  it("names steps within their own sequence", () => {
    for (const [name, seq] of Object.entries(sequences)) {
      for (const step of seq.steps) {
        expect(step).toMatch(new RegExp(`^${name}\\.[\\w-]+$`));
      }
    }
  });
});
