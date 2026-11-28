import { describe, expect, it } from "vitest";
import { demoSubjectAccess } from "./demo-subject";

describe("demoSubjectAccess", () => {
  it("leaves a viewer who is not a demo subject everything", () => {
    expect(demoSubjectAccess({})).toEqual({
      persona: null,
      canWriteStaffRecords: true,
      canReadTeams: true,
    });
  });

  it("offers the clinic persona the staff writes the API admits and the teams it reads", () => {
    expect(
      demoSubjectAccess({
        refusedAsDemoSubject: true,
        refusedStaffWritesAsDemoSubject: false,
        demoPersona: "clinic",
      })
    ).toEqual({
      persona: "clinic",
      canWriteStaffRecords: true,
      canReadTeams: true,
    });
  });

  it("withholds the staff writes from the clinic persona once the demo refuses them, but not the teams", () => {
    expect(
      demoSubjectAccess({
        refusedAsDemoSubject: true,
        refusedStaffWritesAsDemoSubject: true,
        demoPersona: "clinic",
      })
    ).toEqual({
      persona: "clinic",
      canWriteStaffRecords: false,
      canReadTeams: true,
    });
  });

  it("refuses the personal persona both", () => {
    expect(
      demoSubjectAccess({
        refusedAsDemoSubject: true,
        refusedStaffWritesAsDemoSubject: true,
      })
    ).toEqual({
      persona: "personal",
      canWriteStaffRecords: false,
      canReadTeams: false,
    });
  });
});
