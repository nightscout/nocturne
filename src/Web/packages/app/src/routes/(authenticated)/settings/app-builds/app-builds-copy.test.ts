import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { rateLimitedError } from "#lib/forms/submit-messages.svelte.js";
import { describeError, errorText } from "./app-builds-copy.svelte";

const exceptionSource = readFileSync(
  new URL(
    "../../../../../../../../API/Nocturne.API/Services/AppBuilds/AppBuildException.cs",
    import.meta.url
  ),
  "utf8"
);
const codes = [
  ...exceptionSource.matchAll(/public const string \w+ = "([a-z_]+)";/g),
].map((match) => match[1]);

describe("app build error copy", () => {
  it("reads every code AppBuildException declares", () => {
    expect(codes.length).toBeGreaterThan(20);
  });

  it.each(codes)("has a sentence for %s", (code) => {
    expect(errorText(code)).toEqual(expect.any(String));
    expect(describeError({ status: 400, body: { message: code } })).toBe(
      errorText(code)
    );
  });

  it("answers the statuses the generated wrappers forward without a code", () => {
    const limited = describeError({
      status: 429,
      body: { message: "Too many requests" },
    });
    expect(limited).not.toMatch(/GitHub/);
    expect(limited).toBe(rateLimitedError());
    expect(
      describeError({ status: 404, body: { message: "Not found" } })
    ).toMatch(/Reload the page/);
    expect(describeError(new Error("network"))).toMatch(/Try again/);
  });

  it("answers a 403 without a known code as a permission refusal", () => {
    expect(describeError({ status: 403, body: { message: "Forbidden" } })).toBe(
      errorText("forbidden")
    );
  });

  it("does not answer an unknown code with its text", () => {
    expect(errorText("constructor")).toBeUndefined();
    expect(
      describeError({ status: 400, body: { message: "a server sentence" } })
    ).not.toContain("a server sentence");
  });
});
