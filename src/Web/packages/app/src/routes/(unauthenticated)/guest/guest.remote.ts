import { form, getRequestEvent } from "$app/server";
import { invalid, redirect } from "@sveltejs/kit";
import { z } from "zod";
import { errorStatus } from "$lib/forms/submit-error";

/**
 * Redeem a guest code and start the 48-hour read-only session.
 *
 * The API answers 400 for every rejected code — expired, revoked, already used
 * and mistyped are deliberately indistinguishable, so the message covers all
 * four. A transport or server fault gets its own message so "we couldn't check"
 * isn't reported as "your code is wrong".
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

    try {
      await apiClient.guestLink.activateGuestLink({ code: data.code });
    } catch (err) {
      const status = errorStatus(err);

      if (status === 400) {
        invalid(
          issue.code(
            "That code didn't work. It may have expired, already been used, or been entered slightly differently. Ask whoever shared it to send a new one."
          )
        );
      }

      if (status === 429) {
        invalid(
          issue.code("Too many attempts. Please wait a few minutes and try again.")
        );
      }

      // Never log the code itself — it grants access to health data.
      console.error("Guest link activation failed with status:", status ?? "none");
      invalid(
        issue.code("We couldn't check that code just now. Please try again in a moment.")
      );
    }

    redirect(303, "/");
  }
);
