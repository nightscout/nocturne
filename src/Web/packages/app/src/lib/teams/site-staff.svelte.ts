/** A staff member waits for an owner of the site to approve them, mirroring TeamSiteStaffStatus. */
export const STAFF_PENDING = "pending";

/** An owner of the site approved the staff member, mirroring TeamSiteStaffStatus. */
export const STAFF_APPROVED = "approved";

/** An owner of the site declined the staff member, mirroring TeamSiteStaffStatus. */
export const STAFF_DECLINED = "declined";

/** An owner of the site approves each staff member, mirroring TeamStaffRule. */
export const RULE_OWNER_APPROVES = "owner_approves";

/** The team decides, and the site's owners hear about new staff, mirroring TeamStaffRule. */
export const RULE_TEAM_DECIDES = "team_decides";

/** The site trusts every staff member of the team, mirroring TeamStaffRule. */
export const RULE_TEAM_TRUSTED = "team_trusted";
