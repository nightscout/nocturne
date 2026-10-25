import { describe, expect, it } from "vitest";
import { docsUrl } from "./docs-url";

describe("docsUrl", () => {
  it("tags a docs link with where in the app it sits", () => {
    expect(docsUrl("/docs/data-sources/google-health", "google-health-unavailable")).toBe(
      "https://getnocturne.dev/docs/data-sources/google-health?utm_source=nocturne-app&utm_medium=app&utm_content=google-health-unavailable"
    );
  });
});
