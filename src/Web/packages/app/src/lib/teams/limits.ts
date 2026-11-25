import {
  CreateTeamAccessLinkRequestSchema,
  CreateTeamInviteRequestSchema,
  CreateTeamStaffInviteRequestSchema,
  TeamNameRequestSchema,
} from "@nocturne/api-client/schemas";

function hasUnwrap(value: unknown): value is { unwrap(): unknown } {
  return (
    typeof value === "object" && value !== null && "unwrap" in value && typeof value.unwrap === "function"
  );
}

function hasInput(value: unknown): value is { in: unknown } {
  return typeof value === "object" && value !== null && "in" in value;
}

/** The longest string a schema field accepts, through any optional, nullable or transform wrapper. */
function maxLengthOf(field: unknown): number | undefined {
  let current = field;
  while (hasUnwrap(current) || hasInput(current)) {
    current = hasUnwrap(current) ? current.unwrap() : current.in;
  }
  return typeof current === "object" &&
    current !== null &&
    "maxLength" in current &&
    typeof current.maxLength === "number"
    ? current.maxLength
    : undefined;
}

export const teamNameMaxLength = maxLengthOf(TeamNameRequestSchema.shape.name);

export const inviteLabelMaxLength = maxLengthOf(CreateTeamInviteRequestSchema.shape.label);

export const accessLinkLabelMaxLength = maxLengthOf(CreateTeamAccessLinkRequestSchema.shape.label);

export const staffInviteLabelMaxLength = maxLengthOf(CreateTeamStaffInviteRequestSchema.shape.label);
