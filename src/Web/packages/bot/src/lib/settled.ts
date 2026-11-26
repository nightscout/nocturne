import { createLogger } from "./logger.js";

const logger = createLogger();

/**
 * Wraps a command or action handler so its failure is logged and goes no further. The chat SDK
 * passes a handler's failure back to the adapter, and Telegram's long polling then queues the
 * update again without limit, so a handler that fails once (a refused private message, an API
 * outage) would run every 30 seconds for as long as the bot runs.
 */
export function settled<E>(name: string, handler: (event: E) => Promise<void>): (event: E) => Promise<void> {
  return async (event) => {
    try {
      await handler(event);
    } catch (err) {
      logger.error(`The ${name} handler failed:`, err);
    }
  };
}
