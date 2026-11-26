import type { Cookies } from "@sveltejs/kit";
import { runWithContext, type BotOptions, type BotRequestContext } from "@nocturne/bot";
import { getBot } from "./index.js";
import { buildScopedBotApiClient, buildUnscopedBotApiClient } from "./api-client.js";
import { getEffectiveHost, getOriginalProto } from "#lib/server/request-host.js";

type Platform = keyof NonNullable<BotOptions["platforms"]>;

interface WebhookRequest {
  request: Request;
  cookies: Cookies;
  fetch: typeof fetch;
}

/**
 * The context every bot handler reads its API clients from. The bot is a trusted service, so it
 * uses instance-key clients, never `locals.apiClient`, which carries only the end user's
 * credentials. The tenant is resolved later, by `requireLink`, from the directory lookup.
 */
export function botRequestContext({ request, cookies, fetch }: WebhookRequest): BotRequestContext {
  const extraHeaders: Record<string, string> = {
    "X-Forwarded-Proto": getOriginalProto(request),
  };
  const host = getEffectiveHost(request, cookies);
  if (host) extraHeaders["X-Forwarded-Host"] = host;

  return {
    unscopedApi: buildUnscopedBotApiClient(fetch, extraHeaders),
    scopedApiFactory: (tenantSlug: string) => buildScopedBotApiClient(fetch, tenantSlug),
    resolvedTenantSlug: null,
    resolvedLink: null,
  };
}

/**
 * Hands a platform's webhook request to its adapter inside a {@link botRequestContext}, or
 * answers 404 when that platform is not enabled.
 *
 * Adapters run message, slash command and action handlers detached from the request (Discord
 * defers the interaction response; WhatsApp answers Meta before the handler finishes). The
 * context still reaches them, because AsyncLocalStorage follows async-task inheritance. That
 * relies on adapter-node keeping the event loop alive after the response is sent: a serverless
 * adapter would kill the detached tasks and need a `waitUntil` equivalent.
 */
export async function handleBotWebhook(platform: Platform, event: WebhookRequest): Promise<Response> {
  const bot = await getBot();
  const webhook = bot.webhooks[platform];
  if (!webhook) {
    return new Response(`The ${platform} bot is not enabled`, { status: 404 });
  }
  return runWithContext(botRequestContext(event), () => webhook(event.request));
}
