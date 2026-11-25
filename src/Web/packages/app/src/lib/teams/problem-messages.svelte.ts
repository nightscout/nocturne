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
    team_access_role_invalid: "A link cannot suggest this role. Choose another role.",
    team_access_end_invalid: "The end date must be in the future. Choose a later date.",
    team_staff_ceiling_invalid: "Staff cannot be limited to this role. Choose another role.",
    team_staff_end_invalid: "The end date must be in the future. Choose a later date.",
    team_staff_invite_end_passed:
      "The end date that this invite gives has passed. Ask a manager of the team for a new link.",
  };
}

/**
 * The sentence for each problem code the access link page can meet. It words the four codes a
 * manager invite shares with an access link for a link that gives access to a site.
 */
export function teamAccessProblemMessages(): ProblemCodeMessages {
  return {
    ...teamProblemMessages(),
    team_invite_expired: "This link has expired. Ask a manager of the team for a new link.",
    team_invite_revoked: "A manager revoked this link. Ask a manager of the team for a new link.",
    team_invite_used_up: "This link has no uses left. Ask a manager of the team for a new link.",
    team_invite_no_longer_valid:
      "The manager who made this link left the team. Ask a manager of the team for a new link.",
    team_access_link_end_passed:
      "The latest end date that this link allows has passed. Ask a manager of the team for a new link.",
    team_access_already_given: "This team already has access to this site.",
    team_access_not_owner: "Only an owner of the site can give a team access to it.",
    team_access_roles_required: "Choose at least one role.",
    team_access_role_not_in_site: "A role that you chose is not a role of this site. Reload the page and try again.",
    team_access_role_administrative: "A team can have only roles for data. Choose other roles.",
    team_access_role_wider_than_link: "A role that you chose gives more than this link allows. Choose other roles.",
    team_access_role_wider_than_approver: "A role that you chose gives more than you hold. Choose other roles.",
    team_access_limit_required: "This access must be limited to the last 24 hours.",
    team_access_end_later_than_link:
      "The access must end no later than this link allows. Choose an earlier end date.",
    team_access_end_outlasts_approver:
      "The access cannot last longer than your own access. Choose an earlier end date.",
  };
}

/**
 * The sentence for each problem code an owner's decision about a team's staff at their site can meet:
 * approve, decline, and the switch for the team's own staff decisions.
 */
export function teamStaffDecisionProblemMessages(): ProblemCodeMessages {
  return {
    team_staff_decision_not_owner: "Only an owner of this site can decide which staff of a team can see this site.",
    caller_access_from_team:
      "You have access to this site only through a team, so you cannot change the access of a team or its staff.",
  };
}

/**
 * The sentence for each problem code an owner's edit of a team's access to their site can meet. The
 * approval's sentences read the same for an edit, except the refusal of a person who is not an owner.
 */
export function teamAccessEditProblemMessages(): ProblemCodeMessages {
  return {
    ...teamAccessProblemMessages(),
    ...teamStaffDecisionProblemMessages(),
    team_access_not_owner: "Only an owner of this site can change the access of a team.",
  };
}
