import { createPostgresState } from "@chat-adapter/state-pg";

/** The SDK keeps a thread's history under this list key prefix. */
const HISTORY_KEY_PREFIX = "msg-history:";

/**
 * Deletes the thread history the SDK kept before the bot turned it off. The history held the
 * text of every message in a thread, alert cards and people's own messages included, and the
 * bot's state tables are not tenant-scoped and must hold no health data. History is off for
 * every adapter in {@link createBot}, so nothing writes these rows again. The rows do not
 * expire unless their thread is written to, so this runs whether or not any platform is on.
 */
export async function purgeThreadHistory(postgresUrl: string): Promise<void> {
  const state = createPostgresState({ url: postgresUrl });
  await state.connect();
  try {
    await state.getClient().query("DELETE FROM chat_state_lists WHERE list_key LIKE $1", [`${HISTORY_KEY_PREFIX}%`]);
  } finally {
    await state.disconnect();
  }
}
