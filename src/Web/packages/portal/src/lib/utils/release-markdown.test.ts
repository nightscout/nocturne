import { describe, expect, it } from "vitest";
import { renderReleaseMarkdown } from "./release-markdown";

describe("renderReleaseMarkdown", () => {
  it("renders raw HTML as text", () => {
    const html = renderReleaseMarkdown('Fix <img src=x onerror="alert(1)"> and <script>alert(1)</script>');
    expect(html).not.toContain("<img");
    expect(html).not.toContain("<script");
    expect(html).toContain("&lt;img");
  });

  it("keeps a generic in a PR title visible", () => {
    expect(renderReleaseMarkdown("Type Dialog<Text> props")).toContain("Dialog&lt;Text&gt;");
  });

  it("drops links and images to unsafe protocols but keeps their text", () => {
    const html = renderReleaseMarkdown("[click](javascript:alert(1)) ![pic](JaVaScRiPt:x)");
    expect(html).not.toMatch(/javascript:/i);
    expect(html).toContain("click");
    expect(html).toContain("pic");
  });

  it("renders markdown and safe links", () => {
    const html = renderReleaseMarkdown("**bold** [PR](https://github.com/nightscout/nocturne/pull/1)");
    expect(html).toContain("<strong>bold</strong>");
    expect(html).toContain('<a href="https://github.com/nightscout/nocturne/pull/1">PR</a>');
  });

  it("returns nothing for an empty body", () => {
    expect(renderReleaseMarkdown(null)).toBe("");
  });
});
