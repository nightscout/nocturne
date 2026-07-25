import { form, getRequestEvent } from "$app/server";
import { invalid, redirect } from "@sveltejs/kit";
import { z } from "zod";
import {
  classifyActivationError,
  type ActivationFailure,
} from "./activation-error";

const FAILURE_MESSAGES: Record<ActivationFailure, string> = {
  rejected:
    "That code didn't work. It may have expired, already been used, or been entered slightly differently. Ask whoever shared it to send a new one.",
  "rate-limited":
    "Too many attempts. Please wait a few minutes and try again.",
  unavailable:
    "We couldn't check that code just now. Please try again in a moment.",
};

/**
 * Redeem a guest code and start the 48-hour read-only session.
 *
 * The session cookie is set by the API and forwarded to the browser by
 * propagateAuthCookies.
 */
export const activateGuestCode = form(
  z.object({
    code: z.string().trim().min(1, "Enter the code you were given"),
  }),
  async (data, issue) => {
    const { apiClient } = getRequestEvent().locals;

    let failure: ActivationFailure | null = null;

    try {
      await apiClient.guestLink.activateGuestLink({ code: data.code });
    } catch (err) {
      failure = classifyActivationError(err);
      // Never log the code itself — it grants access to health data.
      if (failure === "unavailable") {
        console.error("Guest link activation could not be completed");
      }
    }

    if (failure) invalid(issue.code(FAILURE_MESSAGES[failure]));

    redirect(303, "/");
  }
);
