import { Chat } from "chat";
import type { Adapter } from "chat";
import { createPostgresState } from "@chat-adapter/state-pg";
import { createLogger } from "./lib/logger.js";
import { PLATFORMS, type PlatformSettings } from "./platforms/registry.js";

export type { PlatformCredentials } from "./platforms/platform.js";

const logger = createLogger();

export interface BotOptions {
  platforms?: PlatformSettings;
  postgresUrl: string;
}

export function createBot(options: BotOptions): Chat {
  const adapters: Record<string, Adapter> = {};

  for (const platform of PLATFORMS) {
    const setting = options.platforms?.[platform.name];
    if (!setting) continue;
    logger.info(`Enabling ${platform.displayName} adapter`);
    adapters[platform.name] = platform.createAdapter(typeof setting === "object" ? setting : undefined);
  }

  if (Object.keys(adapters).length === 0) {
    logger.warn("No platform adapters configured.");
  }

  return new Chat({
    userName: "nocturne",
    adapters,
    // A typed command holds its thread for its API calls; the default drops a second command sent
    // meanwhile, or each but one of a batch Meta delivers again after an outage.
    concurrency: "concurrent",
    state: createPostgresState({ url: options.postgresUrl }),
  });
}
