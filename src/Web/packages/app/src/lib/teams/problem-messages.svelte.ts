import type { ProblemCodeMessages } from "#lib/forms/submit-error.js";

/** The sentence for each problem code the team routes answer with, mirroring TeamProblemCodes. */
export function teamProblemMessages(): ProblemCodeMessages {
  return {
    team_name_invalid: "The team name is empty or too long. Change the name and try again.",
    team_invite_label_invalid: "The label is too long. Use a shorter label.",
    team_invite_expiry_invalid: "Nocturne does not allow this expiry. Select another expiry.",
    team_invite_uses_invalid: "Nocturne does not allow this number of uses. Try again.",
    team_last_manager: "A team must keep one manager. Invite another manager first.",
    team_creation_restricted: "The operator of this site limits who can create a team.",
    team_delete_refused: "You cannot delete this team now.",
    team_invite_expired: "This invite link has expired. Ask a manager of the team for a new link.",
    team_invite_revoked: "A manager revoked this invite link. Ask a manager of the team for a new link.",
    team_invite_used_up: "This invite link has no uses left. Ask a manager of the team for a new link.",
    team_invite_no_longer_valid:
      "The manager who made this link left the team. Ask a manager of the team for a new link.",
    team_invite_already_member: "You are already a member of this team.",
  };
}
