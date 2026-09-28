import { describe, expect, it } from "vitest";
import { PatientRelationship } from "$api";
import { patientVoice } from "./patient-voice.svelte";

describe("patientVoice", () => {
  it("speaks to the patient when Nocturne is for the onboarder", () => {
    expect(
      patientVoice({ relationship: PatientRelationship.Self, patientName: "Sam" })
    ).toEqual({ possessive: "your", Possessive: "Your" });
  });

  it.each([PatientRelationship.Caregiver, PatientRelationship.Helper])(
    "names the patient for a %s",
    (relationship) => {
      expect(patientVoice({ relationship, patientName: " Sam " })).toEqual({
        possessive: "Sam's",
        Possessive: "Sam's",
      });
    }
  );

  it.each([
    ["nobody has answered", undefined],
    ["the answer is unset", { patientName: "Sam" }],
    [
      "a caregiver gave no name",
      { relationship: PatientRelationship.Caregiver, patientName: "  " },
    ],
  ])("stays neutral when %s", (_, answer) => {
    expect(patientVoice(answer)).toEqual({ possessive: "the", Possessive: "The" });
  });
});
