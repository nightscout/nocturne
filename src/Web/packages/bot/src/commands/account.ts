import type { Chat } from "chat";
import { getUnscopedApi } from "../lib/request-context.js";
import { requireLink } from "../lib/require-link.js";
import { createLogger } from "../lib/logger.js";
import { settled } from "../lib/settled.js";
import { slashCommandHandle } from "../lib/platform-handle.js";

const logger = createLogger();

const SLUG_RE = /^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]?$/;

export function registerAccountCommands(bot: Chat, baseDomain: string) {
  bot.onSlashCommand("/connect", settled("/connect", async (event) => {
    const slugArg = event.text?.trim().toLowerCase() || null;

    if (slugArg && !SLUG_RE.test(slugArg)) {
      await event.channel.postEphemeral(
        event.user,
        "Invalid instance slug. Use lowercase letters, digits, and hyphens (e.g. `myfamily`).",
        { fallbackToDM: true },
      );
      return;
    }

    try {
      const api = getUnscopedApi();
      const platform = event.adapter.name;
      const { token } = await api.pendingLinks.create(
        platform,
        event.user.userId,
        slashCommandHandle(event),
        slugArg,
        "connect-slash",
      );

      const link = slugArg
        ? `https://${slugArg}.${baseDomain}/auth/bot/authorize?state=${token}`
        : `https://${baseDomain}/auth/bot/authorize?state=${token}`;

      const message = slugArg
        ? `To connect your Nocturne account on **${slugArg}**, click here: ${link}\n\nThis link expires in 10 minutes.`
        : `To connect your Nocturne account, click here: ${link}\n\nThis link expires in 10 minutes. If your instance hosts multiple tenants, run \`/connect <your-slug>\` instead.`;

      // Whoever opens the link binds this chat account to their Nocturne account, so it goes to
      // the user alone, never to a channel others can read. Telegram has no ephemeral messages and
      // refuses a direct message to a user who has not started a private chat with the bot.
      const sent = await event.channel
        .postEphemeral(event.user, message, { fallbackToDM: true })
        .catch((err: unknown) => {
          logger.warn("Could not send the /connect link privately:", err);
          return null;
        });
      if (!sent) {
        await event.channel.post(
          "I could not send you the link privately. Send `/connect` to me in a direct message.",
        );
      }
    } catch (err) {
      logger.error("Error handling /connect command:", err);
      await event.channel.postEphemeral(
        event.user,
        "Failed to start the link flow. Please try again.",
        { fallbackToDM: true },
      );
    }
  }));

  bot.onSlashCommand("/disconnect", settled("/disconnect", async (event) => {
    await requireLink(event, async (link) => {
      try {
        const api = getUnscopedApi();
        await api.directory.revokeByPlatformUser(
          link.id,
          event.adapter.name,
          event.user.userId,
        );
        await event.channel.postEphemeral(
          event.user,
          `Disconnected **${link.displayName}** (\`${link.label}\`).`,
          { fallbackToDM: true },
        );
      } catch (err) {
        logger.error("Error handling /disconnect command:", err);
        await event.channel.postEphemeral(
          event.user,
          "Failed to disconnect. Please try again.",
          { fallbackToDM: true },
        );
      }
    });
  }));

  bot.onSlashCommand("/status", settled("/status", async (event) => {
    await requireLink(event, async (link) => {
      const defaultBadge = "";
      await event.channel.postEphemeral(
        event.user,
        `Linked to **${link.displayName}** (\`${link.label}\`)${defaultBadge}.`,
        { fallbackToDM: true },
      );
    });
  }));
}
