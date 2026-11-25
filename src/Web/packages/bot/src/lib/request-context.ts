import { AsyncLocalStorage } from "node:async_hooks";
import type { BotApiClient } from "../types.js";

export interface BotRequestContext {
  /** Cross-tenant client used for directory/pendingLinks calls and by /connect-style handlers that don't need a tenant. */
  unscopedApi: BotApiClient;
  /** Builds a tenant-scoped api client given a tenant subdomain slug. Closure owned by the SvelteKit route. */
  scopedApiFactory: (tenantSlug: string) => BotApiClient;
  /** Set by requireLink after successful resolution. Null while unresolved. */
  resolvedTenantSlug: string | null;
  resolvedLink: ResolvedLink | null;
}

export interface ResolvedLink {
  id: string;
  tenantId: string;
  tenantSlug: string;
  nocturneUserId: string;
  label: string;
  displayName: string;
}

const storage = new AsyncLocalStorage<BotRequestContext>();

/**
 * Run `fn` with a BotRequestContext available to any downstream slash command
 * or action handler via getUnscopedApi(), getScopedApiFactory(), getApi(), and
 * requireLink(). The store propagates through async-task inheritance, so
 * handlers that run detached from the original call (as the Discord adapter
 * does) still see it correctly.
 */
export function runWithContext<T>(context: BotRequestContext, fn: () => Promise<T>): Promise<T>;
export function runWithContext<T>(context: BotRequestContext, fn: () => T): T;
export function runWithContext<T>(
  context: BotRequestContext,
  fn: () => T | Promise<T>,
): T | Promise<T> {
  return storage.run(context, fn);
}

/**
 * Returns the unscoped api client used for cross-tenant operations (directory,
 * pendingLinks). Called by /connect and by requireLink internally.
 */
export function getUnscopedApi(): BotApiClient {
  const ctx = storage.getStore();
  if (!ctx) {
    throw new Error(
      "getUnscopedApi() called outside runWithContext scope — the webhook route must wrap adapter dispatch in runWithContext(context, ...)",
    );
  }
  return ctx.unscopedApi;
}

/**
 * Returns the scoped api client factory. Used by requireLink to build a
 * per-tenant client once a link has been resolved.
 */
export function getScopedApiFactory(): (slug: string) => BotApiClient {
  const ctx = storage.getStore();
  if (!ctx) {
    throw new Error("getScopedApiFactory() called outside runWithContext scope");
  }
  return ctx.scopedApiFactory;
}

/**
 * Returns the resolved tenant-scoped api client. Only works if requireLink has
 * already run and filled in resolvedTenantSlug. Throws otherwise — handlers
 * that call getApi() must wrap their body in requireLink first.
 */
export function getApi(): BotApiClient {
  const ctx = storage.getStore();
  if (!ctx) {
    throw new Error("getApi() called outside runWithContext scope");
  }
  if (!ctx.resolvedTenantSlug) {
    throw new Error(
      "getApi() called before requireLink resolved a tenant. Wrap your handler body in requireLink(event, ...).",
    );
  }
  return ctx.scopedApiFactory(ctx.resolvedTenantSlug);
}

/**
 * Returns the currently resolved link, or null if requireLink hasn't run yet.
 */
export function getResolvedLink(): ResolvedLink | null {
  return storage.getStore()?.resolvedLink ?? null;
}

/**
 * Internal: used by requireLink to nest a fresh context with the resolved
 * tenant filled in. Not meant to be called by slash handlers directly.
 */
export function runWithResolvedLink<T>(
  link: ResolvedLink,
  fn: () => Promise<T>,
): Promise<T>;
export function runWithResolvedLink<T>(link: ResolvedLink, fn: () => T): T;
export function runWithResolvedLink<T>(
  link: ResolvedLink,
  fn: () => T | Promise<T>,
): T | Promise<T> {
  const parent = storage.getStore();
  if (!parent) {
    throw new Error("runWithResolvedLink called outside parent runWithContext scope");
  }
  const child: BotRequestContext = {
    ...parent,
    resolvedTenantSlug: link.tenantSlug,
    resolvedLink: link,
  };
  return storage.run(child, fn);
}
