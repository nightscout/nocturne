import { describe, expect, it } from "vitest";
import { withoutQueryParam, withQueryParam } from "./query-param";

describe("withQueryParam", () => {
  it("replaces every value the path already carried for the name, and keeps the rest", () => {
    expect(withQueryParam("/r?flow=a&range=7d&flow=b#top", "flow", "c")).toBe(
      "/r?range=7d&flow=c#top",
    );
  });

  it("matches the name after decoding, not a prefix of another name", () => {
    expect(withQueryParam("/r?fl%6Fw=a&flows=x", "flow", "c")).toBe("/r?flows=x&flow=c");
  });

  it("encodes the value", () => {
    expect(withQueryParam("/", "next", "/a b&c")).toBe("/?next=%2Fa%20b%26c");
  });
});

describe("withoutQueryParam", () => {
  it("returns nothing for a URL without the name", () => {
    expect(withoutQueryParam(new URL("https://sam.example/?range=7d"), "flow")).toBeNull();
  });

  it("removes every value of the name and keeps the rest", () => {
    expect(
      withoutQueryParam(new URL("https://sam.example/r?flow=a&range=7d&flow=b#top"), "flow")?.href,
    ).toBe("https://sam.example/r?range=7d#top");
  });
});
