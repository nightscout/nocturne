import { AsyncLocalStorage } from "node:async_hooks";
import type { BotApiClient } from "../types.js";

interface BotRequestStore {
  api: BotApiClient;
}

const storage = new AsyncLocalStorage<BotRequestStore>();

/**
 * Run `fn` with a request-scoped BotApiClient available to any downstream
 * slash command or action handler via `getApi()`. The store propagates
 * through async-task inheritance, so handlers that run detached from the
 * original call (as the Discord adapter does) still see it correctly.
 */
export function runWithApi<T>(api: BotApiClient, fn: () => Promise<T>): Promise<T>;
export function runWithApi<T>(api: BotApiClient, fn: () => T): T;
export function runWithApi<T>(api: BotApiClient, fn: () => T | Promise<T>): T | Promise<T> {
  return storage.run({ api }, fn);
}

/**
 * Retrieve the request-scoped BotApiClient. Throws if called outside a
 * `runWithApi` scope — this indicates a handler was triggered without the
 * webhook route setting up context (programmer error).
 */
export function getApi(): BotApiClient {
  const store = storage.getStore();
  if (!store) {
    throw new Error(
      "getApi() called outside runWithApi scope — the webhook route must wrap adapter dispatch in runWithApi(api, ...)",
    );
  }
  return store.api;
}
