/** What saving a recovery email did about the confirmation link. */
export type RecoveryEmailOutcome = "sent" | "unchanged" | "throttled" | "failed";

/** The problem codes of a save that stored the address but sent no link. */
export const SAVED_WITHOUT_LINK: Readonly<Record<string, RecoveryEmailOutcome>> = {
  email_link_throttled: "throttled",
  email_delivery_failed: "failed",
};
