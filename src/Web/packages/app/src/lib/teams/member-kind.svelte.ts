/** The kind of team member the API reports, mirroring TeamMemberKind. */
export const MANAGER_KIND = "manager";

/** The kind of team member who works for the team and does not run it, mirroring TeamMemberKind. */
export const STAFF_KIND = "staff";

/** The badge for what a person is in a team. */
export function memberKindLabel(kind: string | undefined): string {
  if (kind === MANAGER_KIND) return "Manager";
  if (kind === STAFF_KIND) return "Staff";
  return "Member";
}
