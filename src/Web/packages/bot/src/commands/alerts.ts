import type { Chat } from "chat";
import { AcknowledgedCard, ActiveAlertsCard } from "../cards/alert.js";
import { createLogger } from "../lib/logger.js";
import { getApi } from "../lib/request-context.js";
import { requireLink, requireLinkForAction } from "../lib/require-link.js";
import { decodeActionValue } from "../lib/action-value.js";

const logger = createLogger();

const failed = (action: string) => `Failed to ${action}. Please try again.`;

export function registerAlertCommands(bot: Chat) {
  bot.onAction("ack_alert", async (event) => {
    await requireLinkForAction(event, async () => {
      const { excursionId, unreadableExcursion } = decodeActionValue(event.value);
      if (unreadableExcursion) {
        await event.thread?.post(
          "Couldn't tell which alert this button is for. Nothing was acknowledged.",
        );
        return;
      }

      const acknowledgedBy = event.user.fullName ?? "Unknown";
      let detail: string;

      try {
        const api = getApi();

        // A value that names no excursion at all addresses the whole tenant.
        if (!excursionId) {
          await api.alerts.acknowledge({ acknowledgedBy });
          detail = `All alerts acknowledged by ${acknowledgedBy}.`;
        } else {
          await api.alerts.acknowledgeExcursion(excursionId, { acknowledgedBy });
          detail = `By ${acknowledgedBy}. Any other active alerts are untouched.`;
        }
      } catch (err) {
        logger.error("Error acknowledging alert:", err);
        await event.thread?.post(failed("acknowledge"));
        return;
      }

      // The acknowledge has landed, so a confirmation that cannot be posted is
      // not a failure to report back as one.
      try {
        await event.thread?.post(AcknowledgedCard({ detail }));
      } catch (err) {
        logger.error("Acknowledged, but could not confirm in the thread:", err);
      }
    });
  });

  bot.onSlashCommand("/alerts", async (event) => {
    await requireLink(event, async (link) => {
      try {
        const excursions = (await getApi().alerts.getActiveAlerts()) ?? [];

        if (!excursions.length) {
          await event.channel.post(`No active alerts for ${link.displayName}.`);
          return;
        }

        await event.channel.post(ActiveAlertsCard({ excursions }));
      } catch (err) {
        logger.error("Error handling /alerts command:", err);
        await event.channel.post(failed("fetch alerts"));
      }
    });
  });
}
