/** What `page.data` carries of the API's demo-subject gate. */
export interface DemoSubjectGrant {
  refusedAsDemoSubject?: boolean;
  refusedStaffWritesAsDemoSubject?: boolean;
  demoPersona?: string | null;
}

/**
 * The demo's two ways in: the shared person, and a member of staff of the demo
 * clinic.
 */
export type DemoPersona = "personal" | "clinic";

export interface DemoSubjectAccess {
  /**
   * The persona the viewer signed in as, null for a viewer who is not a demo
   * subject.
   */
  persona: DemoPersona | null;
  /** Whether the viewer may keep saved views and personal flag rules. */
  canWriteStaffRecords: boolean;
  /** Whether the viewer may read the teams they belong to. */
  canReadTeams: boolean;
}

const CLINIC_PERSONA = "clinic";

/**
 * What the demo-subject gate leaves a viewer. A demo subject is refused
 * everything the API does not open to it, so every other write stays refused
 * here too: the staff writes (saved views and personal flag rules) are open to
 * the clinic persona while the demo allows them, and the reads of teams to the
 * clinic persona alone, since the personal persona belongs to no team.
 */
export function demoSubjectAccess(grant: DemoSubjectGrant): DemoSubjectAccess {
  const demo = grant.refusedAsDemoSubject === true;
  const persona: DemoPersona | null = !demo
    ? null
    : grant.demoPersona === CLINIC_PERSONA
      ? "clinic"
      : "personal";

  return {
    persona,
    canWriteStaffRecords:
      !demo || grant.refusedStaffWritesAsDemoSubject !== true,
    canReadTeams: !demo || persona === "clinic",
  };
}
