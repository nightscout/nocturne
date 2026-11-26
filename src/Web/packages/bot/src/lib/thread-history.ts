import type { Chat } from "chat";
import { PostgresStateAdapter } from "@chat-adapter/state-pg";

/** The SDK keeps a thread's history under this list key prefix. */
const HISTORY_KEY_PREFIX = "msg-history:";

/**
 * Deletes the thread history the SDK kept before the bot turned it off. The history held the
 * text of every message in a thread, alert cards and people's own messages included, and the
 * bot's state tables are not tenant-scoped and must hold no health data. History is off for
 * every adapter in {@link createBot}, so nothing writes these rows again.
 */
export async function purgeThreadHistory(bot: Chat): Promise<void> {
  const state = bot.getState();
  if (!(state instanceof PostgresStateAdapter)) return;
  await state.connect();
  await state.getClient().query("DELETE FROM chat_state_lists WHERE list_key LIKE $1", [`${HISTORY_KEY_PREFIX}%`]);
}
