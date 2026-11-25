import { describe, expect, it } from "vitest";
import {
  accessLinkLabelMaxLength,
  inviteLabelMaxLength,
  staffInviteLabelMaxLength,
  teamNameMaxLength,
} from "./limits";

describe("team limits", () => {
  it("come from the generated schemas, which carry the API's MaxLength", () => {
    expect(teamNameMaxLength).toBe(100);
    expect(inviteLabelMaxLength).toBe(255);
    expect(accessLinkLabelMaxLength).toBe(255);
    expect(staffInviteLabelMaxLength).toBe(255);
  });
});
