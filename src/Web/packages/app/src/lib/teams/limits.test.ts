import { describe, expect, it } from "vitest";
import { inviteLabelMaxLength, teamNameMaxLength } from "./limits";

describe("team limits", () => {
  it("come from the generated schemas, which carry the API's MaxLength", () => {
    expect(teamNameMaxLength).toBe(100);
    expect(inviteLabelMaxLength).toBe(255);
  });
});
