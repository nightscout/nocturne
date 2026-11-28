import { createLogger } from "./logger.js";

const logger = createLogger();

/**
 * Wraps a command or action handler so its failure is logged under the handler's name and goes no
 * further. No webhook route passes the chat SDK an option to carry a handler's failure back to the
 * adapter, so a failed handler never makes a platform deliver its update again.
 */
export function settled<A extends unknown[]>(
  name: string,
  handler: (...args: A) => Promise<void>,
): (...args: A) => Promise<void> {
  return async (...args) => {
    try {
      await handler(...args);
    } catch (err) {
      logger.error(`The ${name} handler failed:`, err);
    }
  };
}
