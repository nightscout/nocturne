import { errorCode, type ProblemCodeMessages } from "#lib/forms/submit-error.js";

/** The longest name a view may have. */
export const VIEW_NAME_MAX_LENGTH = 100;

const NAME_TAKEN = "people_view_name_taken";

/** Whether a refused view write is refused for its name alone, the one refusal the name field is at fault for. */
export function isNameTaken(err: unknown): boolean {
  return errorCode(err) === NAME_TAKEN;
}

/** What a refused view write says, by the API's problem code. `limit` is the most views a viewer may hold. */
export function viewProblemMessages(limit: number): ProblemCodeMessages {
  return {
    [NAME_TAKEN]: "You have a view with this name. Use a different name.",
    people_view_limit_reached: `You have ${limit} views, the maximum. Delete a view to save a new view.`,
    people_view_order_mismatch: "Your views changed. Try again.",
  };
}
