import { describe, expect, it } from "vitest";
import { issueAge } from "./issue-age";

const minutesAgo = (n: number) => new Date(Date.now() - n * 60000).toISOString();

describe("issueAge", () => {
  it("words a recent update in the given locale", () => {
    const en = issueAge(minutesAgo(5), "en");
    const de = issueAge(minutesAgo(5), "de");
    expect(en).toContain("5");
    expect(de).toContain("5");
    expect(de).not.toBe(en);
  });

  it("gives an absolute date in the given locale from a week on", () => {
    const iso = minutesAgo(60 * 24 * 30);
    expect(issueAge(iso, "en-US")).toBe(new Date(iso).toLocaleDateString("en-US"));
    expect(issueAge(iso, "de")).toBe(new Date(iso).toLocaleDateString("de"));
  });
});
