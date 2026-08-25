import type { ActionEvent, SlashCommandEvent } from "chat";
import { getUnscopedApi, runWithResolvedLink, type ResolvedLink } from "./request-context.js";
import type { DirectoryCandidate } from "../types.js";

interface LinkResolutionInput {
  platform: string;
  platformUserId: string;
  /** Label typed as a command argument. Actions carry no text, so null. */
  labelArg: string | null;
  /** Tenant the invocation is already bound to (a card button's value). Wins over labelArg. */
  tenantId: string | null;
  /** Appended to the ambiguity message to tell the user how to pick. */
  disambiguationHint: string;
}

/**
 * Resolves a chat-platform user to a specific Nocturne tenant link, then
 * invokes `callback` with the resolved link and a nested request context that
 * makes `getApi()` return the tenant-scoped api client. `explain` receives the
 * user-facing message on every non-success branch.
 *
 * This is the chokepoint that every tenant-scoped handler goes through, via
 * {@link requireLink} (slash commands) or {@link requireLinkForAction} (card
 * button taps). Handlers that don't wrap their body in one of those will have
 * `getApi()` throw.
 *
 * ## Resolution logic
 *
 * Calls the directory.resolve endpoint via the unscoped api client, then:
 *
 * - **Zero candidates** → explains "your account isn't linked yet, run
 *   /connect" and returns null without invoking callback.
 *
 * - **`tenantId` given** (the invocation is already bound to a tenant, e.g. an
 *   alert card carries the tenant the alert fired for) → the candidate for that
 *   tenant, or, if the tapping user has no link to it, explains that and
 *   returns null. Never falls back to another tenant.
 *
 * - **One candidate** → ignores any label arg (even if it doesn't match —
 *   assume the user typed a different platform's label by mistake; give them
 *   the only thing they have linked) and invokes callback with that link.
 *
 * - **Multiple candidates + label arg matches exactly one** → invokes
 *   callback with the matched link.
 *
 * - **Multiple candidates + no label arg + exactly one marked default** →
 *   invokes callback with the default link.
 *
 * - **Multiple candidates + no label arg + no single default** → explains the
 *   ambiguity, listing each label and display name, and returns null.
 *
 * - **Multiple candidates + label arg does not match any** → explains "no link
 *   named X, try one of: ..." and returns null.
 *
 * Returns `null` on any non-success branch so callers can detect whether
 * their work actually ran.
 *
 * @example
 * // Zero candidates branch:
 * //   User invokes /bg, never ran /connect.
 * //   Ephemeral: "Your Discord account isn't linked to a Nocturne account
 * //   yet. Run `/connect` to get started."
 *
 * @example
 * // Single candidate branch:
 * //   User has one link labelled "home". They run `/bg wrong-label`.
 * //   Resolves to "home" anyway.
 *
 * @example
 * // Multi + label match:
 * //   User has "home" and "work". They run `/bg work`.
 * //   Resolves to the "work" link.
 *
 * @example
 * // Multi + default:
 * //   User has "home" and "work", with "work" set as their default.
 * //   They run `/bg`. Resolves to "work" without asking.
 *
 * @example
 * // Multi + ambiguous:
 * //   User has "home" and "work", neither set as default. They run `/bg`.
 * //   Ephemeral: "You have multiple linked Nocturne accounts: `home` (Home),
 * //   `work` (Work). Use `/bg <label>` to pick one, or set a default in
 * //   Settings → Integrations → Discord."
 *
 * @example
 * // Multi + label not found:
 * //   User has "home" and "work". They run `/bg beach`.
 * //   Ephemeral: "No linked account named `beach`. Your linked accounts:
 * //   `home`, `work`."
 *
 * @example
 * // Bound tenant branch:
 * //   User has "home" and "work" and taps Acknowledge on a "work" alert.
 * //   Resolves to "work" without asking, because the card carries its tenant.
 */
async function withResolvedLink<T>(
  input: LinkResolutionInput,
  explain: (message: string) => Promise<unknown>,
  callback: (link: ResolvedLink) => Promise<T>,
): Promise<T | null> {
  const candidates = await getUnscopedApi().directory.resolve(
    input.platform,
    input.platformUserId,
  );

  if (!candidates || candidates.length === 0) {
    await explain(
      "Your Discord account isn't linked to a Nocturne account yet. Run `/connect` to get started.",
    );
    return null;
  }

  const picked = pickCandidate(candidates, input.labelArg, input.tenantId);
  const availableLabels = candidates.map((c) => `\`${c.label}\``).join(", ");

  if (picked === "ambiguous") {
    const labelList = candidates
      .map((c) => `\`${c.label}\` (${c.displayName})`)
      .join(", ");
    await explain(
      `You have multiple linked Nocturne accounts: ${labelList}. ${input.disambiguationHint}`,
    );
    return null;
  }

  if (picked === "not-found") {
    await explain(
      `No linked account named \`${input.labelArg}\`. Your linked accounts: ${availableLabels}.`,
    );
    return null;
  }

  if (picked === "tenant-not-linked") {
    await explain(
      `That belongs to a Nocturne account you aren't linked to. Your linked accounts: ${availableLabels}.`,
    );
    return null;
  }

  const link: ResolvedLink = {
    id: picked.id,
    tenantId: picked.tenantId,
    tenantSlug: picked.tenantSlug,
    nocturneUserId: picked.nocturneUserId,
    label: picked.label,
    displayName: picked.displayName,
  };

  return await runWithResolvedLink(link, () => callback(link));
}

/**
 * Slash-command entry point to {@link withResolvedLink}. Takes the optional
 * label argument from `event.text` (trimmed, lowercased) and explains failures
 * as an ephemeral in the invoking channel.
 *
 * @example
 * // Basic tenant-scoped handler
 * bot.onSlashCommand("/bg", async (event) => {
 *   await requireLink(event, async (link) => {
 *     const api = getApi(); // scoped to link.tenantSlug
 *     const result = await api.sensorGlucose.getAll(undefined, undefined, 1);
 *     await event.channel.post(`Latest BG for ${link.displayName}: ${result.data?.[0]?.mgdl}`);
 *   });
 * });
 *
 * @example
 * // Detecting whether the callback actually ran
 * bot.onSlashCommand("/refresh", async (event) => {
 *   const result = await requireLink(event, async (link) => {
 *     return await doWork(link);
 *   });
 *   if (result === null) {
 *     // requireLink already posted an ephemeral explaining what went wrong.
 *     return;
 *   }
 *   // use result...
 * });
 */
export async function requireLink<T>(
  event: SlashCommandEvent,
  callback: (link: ResolvedLink) => Promise<T>,
): Promise<T | null> {
  return await withResolvedLink(
    {
      platform: event.adapter.name,
      platformUserId: event.user.userId,
      labelArg: event.text?.trim().toLowerCase() || null,
      tenantId: null,
      disambiguationHint: `Use \`${event.command} <label>\` to pick one, or set a default in Settings → Integrations → Discord.`,
    },
    (message) =>
      event.channel.postEphemeral(event.user, message, { fallbackToDM: true }),
    callback,
  );
}

/**
 * Card-action entry point to {@link withResolvedLink}. An `ActionEvent` carries
 * no channel, text or command, so the tenant comes from the button's value
 * (cards that address a tenant must set it) and failures are explained as an
 * ephemeral in the thread the card was posted to.
 *
 * @example
 * bot.onAction("ack_alert", async (event) => {
 *   await requireLinkForAction(event, async () => {
 *     await getApi().alerts.acknowledge({ acknowledgedBy: event.user.fullName });
 *   });
 * });
 */
export async function requireLinkForAction<T>(
  event: ActionEvent,
  callback: (link: ResolvedLink) => Promise<T>,
): Promise<T | null> {
  return await withResolvedLink(
    {
      platform: event.adapter.name,
      platformUserId: event.user.userId,
      labelArg: null,
      tenantId: event.value || null,
      disambiguationHint:
        "Set a default in Settings → Integrations → Discord, or use the matching slash command with a label.",
    },
    (message) =>
      event.thread?.postEphemeral(event.user, message, { fallbackToDM: true }) ??
      Promise.resolve(null),
    callback,
  );
}

/**
 * Selects a single DirectoryCandidate from a non-empty list, preferring a bound
 * `tenantId`, then an explicit `labelArg`, then the sole default link. Returns
 * "tenant-not-linked" if `tenantId` names a tenant the user has no link to,
 * "ambiguous" if no disambiguation is possible, or "not-found" if the label arg
 * doesn't match any candidate.
 *
 * Pure function — no side effects, easy to reason about.
 */
function pickCandidate(
  candidates: DirectoryCandidate[],
  labelArg: string | null,
  tenantId: string | null,
): DirectoryCandidate | "ambiguous" | "not-found" | "tenant-not-linked" {
  // A bound tenant is authoritative: acting on a different one would silently
  // hit the wrong patient's data.
  if (tenantId) {
    const wanted = tenantId.toLowerCase();
    return (
      candidates.find((c) => c.tenantId.toLowerCase() === wanted) ??
      "tenant-not-linked"
    );
  }

  // Single candidate: always return it, ignoring any label arg the user typed.
  if (candidates.length === 1) return candidates[0]!;

  // Label provided: exact match or nothing.
  if (labelArg) {
    const match = candidates.find((c) => c.label === labelArg);
    return match ?? "not-found";
  }

  // ux_directory_user_one_default permits at most one default per platform
  // user, so more than one here means the invariant broke — don't guess.
  const defaults = candidates.filter((c) => c.isDefault);
  if (defaults.length === 1) return defaults[0]!;

  return "ambiguous";
}
