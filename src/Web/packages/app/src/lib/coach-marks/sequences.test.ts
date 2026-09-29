import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { ONBOARDING_CORE_GATE, sequences } from "./sequences";

const SRC = fileURLToPath(new URL("../..", import.meta.url));

/** Keys attached with no sequence around them. Each one is eligible on every page load. */
const STANDALONE_KEYS: readonly string[] = [];

interface Attachment {
  file: string;
  /** The literal `key:` of each option object in the call; null for one that is not a literal. */
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
  for (const match of source.matchAll(/\bcoachmark\(/g)) {
    const body = callBody(source, match.index + match[0].length - 1);
    const keys = [...body.matchAll(/\bkey:\s*(?:"([^"]*)"|'([^']*)'|(\S))/g)].map(
      ([, double, single]) => double ?? single ?? null,
    );
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

  it("gates every discovery tour on the onboarding core, not on a sequence", () => {
    for (const [name, seq] of Object.entries(sequences)) {
      if (name === "quick-tour") continue;
      expect(seq.prerequisite, name).toBe(ONBOARDING_CORE_GATE);
    }
    expect(sequences).not.toHaveProperty(ONBOARDING_CORE_GATE);
  });

  it("lets the quick tour run straight after the core", () => {
    expect(sequences["quick-tour"]).not.toHaveProperty("prerequisite");
  });

  it("names steps within their own sequence", () => {
    for (const [name, seq] of Object.entries(sequences)) {
      for (const step of seq.steps) {
        expect(step).toMatch(new RegExp(`^${name}\\.[\\w-]+$`));
      }
    }
  });
});
