/** The kind of team member the API reports, mirroring TeamMemberKind. */
export const MANAGER_KIND = "manager";

/** The badge for what a person is in a team. */
export function memberKindLabel(kind: string | undefined): string {
  return kind === MANAGER_KIND ? "Manager" : "Member";
}
