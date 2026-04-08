# Bot Slash Commands Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Wire `/bg`, `/glucose`, and `/account` (`/connect`, `/disconnect`, `/status`) slash commands end-to-end on Discord, from interaction webhook through to the Nocturne API, and provide a manual script to register the command manifest with Discord.

**Architecture:** Handlers obtain a request-scoped `BotApiClient` via Node `AsyncLocalStorage` populated by the SvelteKit webhook route from `locals.apiClient`. `registerAllCommands` is invoked once at bot-singleton init and takes no api parameter — handlers call `getApi()` per event. A separate manual `pnpm bot:register-discord-commands` script bulk-PUTs a shared manifest to Discord's application commands endpoint.

**Tech Stack:** TypeScript, Node 24, SvelteKit (`adapter-node`), `@nocturne/bot`, `chat` / `@chat-adapter/discord` 4.20.2, pnpm workspaces.

**Branch:** `feat/bot-slash-commands` (already created).

**Pre-merge cleanup:** Delete `docs/plans/2026-04-08-bot-slash-commands.md` as the final commit before merging. `docs/` is not tracked today, but this file will be added during the plan-commit step below — it must come back out.

---

## Context For The Implementing Engineer

Read these files before starting:
- `src/Web/packages/bot/src/commands/index.ts` — current `registerAllCommands` signature (takes `api` — we're removing that).
- `src/Web/packages/bot/src/commands/glucose.ts` — current `/bg` handler using a closed-over `api`.
- `src/Web/packages/bot/src/commands/account.ts` — `/connect`, `/disconnect`, `/status` (`/connect` does NOT need api, the others eventually will).
- `src/Web/packages/bot/src/types.ts` — `BotApiClient` interface shape.
- `src/Web/packages/app/src/lib/server/bot/index.ts` — `getBot()` singleton.
- `src/Web/packages/app/src/routes/api/v4/bot/dispatch/+server.ts` — canonical example of building a `BotApiClient` from `locals.apiClient`; we will extract its inner builder into a shared helper.
- `src/Web/packages/app/src/routes/api/v4/webhooks/discord/+server.ts` — Discord interaction webhook route; currently just delegates to `bot.webhooks.discord(request)`.

Key facts already verified (do not re-verify):
1. The Discord adapter ([`@chat-adapter/discord/dist/index.js:487-491`](../../src/Web/packages/bot/node_modules/@chat-adapter/discord/dist/index.js)) auto-sends `DeferredChannelMessageWithSource` before running slash handlers, so handlers may take longer than 3 seconds and post follow-ups via interaction token. You do **not** need to handle Discord's 3-second deadline.
2. `handleApplicationCommandInteraction` is called without `await` and the handler chain runs detached from the HTTP response. Node `AsyncLocalStorage` still propagates the store through the async-task tree, so ALS set in the webhook route WILL be visible inside handlers.
3. SvelteKit under `adapter-node` (production) keeps the Node event loop alive after the response resolves, so detached async work completes normally. No `waitUntil` is needed. (If Nocturne ever moves to a serverless adapter, revisit.)
4. The `chat` package does NOT auto-register slash commands with Discord. We must PUT them ourselves to `https://discord.com/api/v10/applications/{app_id}/commands` (bulk overwrite).

---

## Task 0: Commit the plan

**Files:**
- Create: `docs/plans/2026-04-08-bot-slash-commands.md` (this file)

**Step 1: Add and commit**

```bash
git add docs/plans/2026-04-08-bot-slash-commands.md
git commit -m "docs: add bot slash commands implementation plan (temporary)"
```

The final task of this plan removes this file. It only lives in git history on this branch.

---

## Task 1: Add AsyncLocalStorage request-context module in `@nocturne/bot`

**Files:**
- Create: `src/Web/packages/bot/src/lib/request-context.ts`
- Modify: `src/Web/packages/bot/src/index.ts` (add export)

**Step 1: Write the module**

```ts
// src/Web/packages/bot/src/lib/request-context.ts
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
```

**Step 2: Re-export from package entrypoint**

Add to `src/Web/packages/bot/src/index.ts`:

```ts
export { runWithApi, getApi } from "./lib/request-context.js";
```

**Step 3: Build**

```bash
cd src/Web/packages/bot && pnpm run build
```

Expected: clean build, no TypeScript errors.

**Step 4: Commit**

```bash
git add src/Web/packages/bot/src/lib/request-context.ts src/Web/packages/bot/src/index.ts
git commit -m "feat(bot): add AsyncLocalStorage request-context for slash command handlers"
```

---

## Task 2: Switch `registerGlucoseCommands` to `getApi()`

**Files:**
- Modify: `src/Web/packages/bot/src/commands/glucose.ts`

**Step 1: Rewrite handler to use `getApi`**

```ts
import type { Chat } from "chat";
import { GlucoseCard } from "../cards/glucose.js";
import { createLogger } from "../lib/logger.js";
import { getApi } from "../lib/request-context.js";

const logger = createLogger();

export function registerGlucoseCommands(bot: Chat) {
  const handleBg = async (channel: { post(msg: any): Promise<any> }) => {
    try {
      const api = getApi();
      const result = await api.sensorGlucose.getAll(undefined, undefined, 1);
      const readings = result.data ?? [];

      if (!readings.length) {
        await channel.post("No recent glucose readings found.");
        return;
      }

      const card = GlucoseCard({ reading: readings[0] });
      await channel.post(card);
    } catch (err) {
      logger.error("Error handling /bg command:", err);
      await channel.post("Failed to fetch glucose data. Please try again.");
    }
  };

  bot.onSlashCommand("/bg", async (event) => handleBg(event.channel));
  bot.onSlashCommand("/glucose", async (event) => handleBg(event.channel));
}
```

Notice the `api` parameter has been removed from the function signature.

**Step 2: Build bot package**

```bash
cd src/Web/packages/bot && pnpm run build
```

Expected: will currently fail because `registerAllCommands` still passes `api` to this function — that is fixed in Task 4.

**Step 3: Commit (don't build yet — combine with Task 3)**

Don't commit yet — commit once `registerAllCommands` in Task 4 compiles cleanly.

---

## Task 3: Switch `registerAlertCommands` to `getApi()`

**Files:**
- Modify: `src/Web/packages/bot/src/commands/alerts.ts`

**Step 1: Rewrite**

```ts
import type { Chat } from "chat";
import { createLogger } from "../lib/logger.js";
import { getApi } from "../lib/request-context.js";

const logger = createLogger();

export function registerAlertCommands(bot: Chat) {
  bot.onAction("ack_alert", async (event) => {
    try {
      const api = getApi();
      await api.alerts.acknowledge({ acknowledgedBy: event.user.fullName ?? "Unknown" });
      await event.thread?.post("All alerts acknowledged.");
    } catch (err) {
      logger.error("Error acknowledging alert:", err);
      await event.thread?.post("Failed to acknowledge. Please try again.");
    }
  });

  bot.onAction("mute_30", async (event) => {
    await event.thread?.post("Muting is not yet available.");
  });

  bot.onSlashCommand("/alerts", async (event) => {
    await event.channel.post("Alert status display coming soon.");
  });
}
```

**Important:** `onAction` handlers (button clicks from alert cards) will now also run inside `runWithApi`. The webhook route wraps the entire `adapter.handleWebhook(...)` call, which includes button interactions, not just slash commands.

---

## Task 4: Update `registerAllCommands` signature

**Files:**
- Modify: `src/Web/packages/bot/src/commands/index.ts`

**Step 1: Drop the `api` parameter**

```ts
import type { Chat } from "chat";
import { registerGlucoseCommands } from "./glucose.js";
import { registerAccountCommands } from "./account.js";
import { registerAlertCommands } from "./alerts.js";

export function registerAllCommands(bot: Chat, nocturneUrl: string) {
  registerGlucoseCommands(bot);
  registerAccountCommands(bot, nocturneUrl);
  registerAlertCommands(bot);
}
```

**Step 2: Build bot package**

```bash
cd src/Web/packages/bot && pnpm run build
```

Expected: clean. If anything else in the package still imports `BotApiClient` purely for the old signature, remove those imports.

**Step 3: Commit Tasks 2, 3, and 4 together**

```bash
git add src/Web/packages/bot/src/commands/glucose.ts \
        src/Web/packages/bot/src/commands/alerts.ts \
        src/Web/packages/bot/src/commands/index.ts
git commit -m "refactor(bot): command handlers fetch api from request context"
```

---

## Task 5: Extract shared `buildBotApiClient` helper

The existing `/api/v4/bot/dispatch/+server.ts` already constructs a `BotApiClient` from `locals.apiClient`. We need the same shape in the Discord webhook route. DRY it.

**Files:**
- Create: `src/Web/packages/app/src/lib/server/bot/api-client.ts`
- Modify: `src/Web/packages/app/src/routes/api/v4/bot/dispatch/+server.ts`

**Step 1: Create the helper**

```ts
// src/Web/packages/app/src/lib/server/bot/api-client.ts
import type { BotApiClient } from "@nocturne/bot";
import type { ApiClient } from "$lib/api/generated/nocturne-api-client";

/**
 * Adapts the NSwag-generated request-scoped ApiClient (from `locals.apiClient`)
 * to the minimal `BotApiClient` surface that the bot package consumes.
 * Used by the bot dispatch endpoint and the platform interaction webhooks.
 */
export function buildBotApiClient(api: ApiClient): BotApiClient {
  return {
    sensorGlucose: {
      getAll: (from, to, limit, offset, sort, device, source, signal) =>
        api.sensorGlucose.getAll(from, to, limit, offset, sort, device, source, signal),
    },
    alerts: {
      acknowledge: (request, signal) => api.alerts.acknowledge(request, signal),
      markDelivered: (deliveryId, request, signal) =>
        api.alerts.markDelivered(deliveryId, request, signal),
      markFailed: (deliveryId, request, signal) =>
        api.alerts.markFailed(deliveryId, request, signal),
      getPendingDeliveries: (channelType, signal) =>
        api.alerts.getPendingDeliveries(channelType, signal),
    },
    chatIdentity: {
      resolve: (platform, platformUserId, signal) =>
        api.chatIdentity.resolve(platform, platformUserId, signal),
      createLink: (request, signal) => api.chatIdentity.createLink(request, signal),
    },
    system: {
      heartbeat: (request, signal) => api.system.heartbeat(request, signal),
    },
  };
}
```

**Note:** Verify the actual `ApiClient` import path by opening `src/Web/packages/app/src/lib/api/generated/nocturne-api-client.ts` (or wherever `locals.apiClient`'s type is defined — check `src/Web/packages/app/src/app.d.ts`). Adjust the import accordingly. If the generated client's method signatures differ from the `BotApiClient` surface, wrap them as the existing dispatch endpoint does rather than forwarding directly — the existing dispatch code is the source of truth for correct adaptation.

**Step 2: Update the dispatch endpoint to use the helper**

Edit `src/Web/packages/app/src/routes/api/v4/bot/dispatch/+server.ts`:

```ts
import type { RequestHandler } from "./$types";
import { handleBotDispatch } from "$lib/server/bot";
import { buildBotApiClient } from "$lib/server/bot/api-client";
import type { AlertDispatchEvent } from "@nocturne/bot";

export const POST: RequestHandler = async ({ request, locals }) => {
  try {
    const event: AlertDispatchEvent = await request.json();
    const botApiClient = buildBotApiClient(locals.apiClient);
    await handleBotDispatch(event, botApiClient);
    return new Response(null, { status: 204 });
  } catch (err) {
    console.error("Bot dispatch failed:", err);
    return new Response(JSON.stringify({ error: "Dispatch failed" }), {
      status: 500,
      headers: { "Content-Type": "application/json" },
    });
  }
};
```

**Step 3: Typecheck**

```bash
cd src/Web/packages/app && pnpm run check
```

Expected: no new errors compared to baseline. If `buildBotApiClient` has shape mismatches, port the exact wrapping logic from the old dispatch endpoint verbatim.

**Step 4: Commit**

```bash
git add src/Web/packages/app/src/lib/server/bot/api-client.ts \
        src/Web/packages/app/src/routes/api/v4/bot/dispatch/+server.ts
git commit -m "refactor(web): extract buildBotApiClient helper"
```

---

## Task 6: Wire `registerAllCommands` into the `getBot()` singleton

**Files:**
- Modify: `src/Web/packages/app/src/lib/server/bot/index.ts`

**Step 1: Call `registerAllCommands` at init**

```ts
import { createBot, registerAllCommands, AlertDeliveryHandler, type BotOptions } from "@nocturne/bot";
import type { BotApiClient, AlertDispatchEvent } from "@nocturne/bot";
import { env } from "$env/dynamic/private";

type Bot = ReturnType<typeof createBot>;

let botInstance: Bot | null = null;

export function getBot(): Bot {
  if (!botInstance) {
    const options: BotOptions = {
      platforms: {
        discord: !!env.DISCORD_BOT_TOKEN,
        slack: !!env.SLACK_BOT_TOKEN && !!env.SLACK_SIGNING_SECRET,
        telegram: !!env.TELEGRAM_BOT_TOKEN,
        whatsapp: !!env.WHATSAPP_ACCESS_TOKEN,
      },
      postgresUrl: process.env["ConnectionStrings__nocturne-postgres"] ?? "",
    };
    botInstance = createBot(options);
    registerAllCommands(botInstance, env.PUBLIC_NOCTURNE_URL ?? env.NOCTURNE_URL ?? "");
  }
  return botInstance;
}

export async function handleBotDispatch(event: AlertDispatchEvent, api: BotApiClient): Promise<void> {
  const bot = getBot();
  const handler = new AlertDeliveryHandler(bot, api);
  await handler.deliver(event);
}
```

**Note on `nocturneUrl`:** Check what env var the project already uses for the public base URL. Search for `PUBLIC_NOCTURNE_URL`, `ORIGIN`, or similar in `appsettings.example.json` and `.env` files. Use whatever is canonical — don't invent a new variable. The `/connect` command uses this to build the bot authorize link.

**Step 2: Typecheck**

```bash
cd src/Web/packages/app && pnpm run check
```

**Step 3: Commit**

```bash
git add src/Web/packages/app/src/lib/server/bot/index.ts
git commit -m "feat(web): register bot slash commands at singleton init"
```

---

## Task 7: Wrap the Discord webhook route in `runWithApi`

**Files:**
- Modify: `src/Web/packages/app/src/routes/api/v4/webhooks/discord/+server.ts`

**Step 1: Rewrite the route**

```ts
import type { RequestHandler } from "./$types";
import { getBot } from "$lib/server/bot";
import { runWithApi } from "@nocturne/bot";
import { buildBotApiClient } from "$lib/server/bot/api-client";

export const POST: RequestHandler = async ({ request, locals }) => {
  const bot = getBot();
  const botApiClient = buildBotApiClient(locals.apiClient);

  // IMPORTANT: The Discord adapter auto-defers the interaction response and
  // runs slash/action handlers detached from this request. Node ALS propagates
  // through async-task inheritance, so handlers called inside this runWithApi
  // scope will see botApiClient via getApi() even after we return.
  //
  // This relies on adapter-node keeping the event loop alive after the response
  // is sent. If Nocturne ever moves to a serverless SvelteKit adapter, the
  // detached tasks will be killed and this needs a waitUntil-equivalent.
  return runWithApi(botApiClient, () => bot.webhooks.discord(request));
};
```

**Step 2: Typecheck**

```bash
cd src/Web/packages/app && pnpm run check
```

**Step 3: Commit**

```bash
git add src/Web/packages/app/src/routes/api/v4/webhooks/discord/+server.ts
git commit -m "feat(web): wrap Discord interactions in runWithApi scope"
```

---

## Task 8: Define the command manifest

**Files:**
- Create: `src/Web/packages/bot/src/commands/manifest.ts`
- Modify: `src/Web/packages/bot/src/index.ts` (export manifest)

**Step 1: Write the manifest**

```ts
// src/Web/packages/bot/src/commands/manifest.ts

/**
 * Discord application command definitions.
 * Shape matches Discord's POST/PUT /applications/{id}/commands schema.
 * See: https://docs.discord.com/developers/interactions/application-commands
 *
 * Keep this in sync with the handlers registered in ./index.ts. This manifest
 * is consumed by `scripts/register-discord-commands.ts` to bulk-PUT commands
 * to Discord. Removing an entry here and re-running the script will delete
 * the command from Discord.
 */
export interface SlashCommandDefinition {
  name: string;
  description: string;
  type?: 1; // CHAT_INPUT
}

export const DISCORD_COMMAND_MANIFEST: SlashCommandDefinition[] = [
  { name: "bg", description: "Show your latest glucose reading" },
  { name: "glucose", description: "Show your latest glucose reading" },
  { name: "connect", description: "Link your Discord account to Nocturne" },
  { name: "disconnect", description: "Unlink your Discord account from Nocturne" },
  { name: "status", description: "Show your Nocturne account status" },
];
```

**Step 2: Export from package**

Add to `src/Web/packages/bot/src/index.ts`:

```ts
export { DISCORD_COMMAND_MANIFEST, type SlashCommandDefinition } from "./commands/manifest.js";
```

**Step 3: Build**

```bash
cd src/Web/packages/bot && pnpm run build
```

**Step 4: Commit**

```bash
git add src/Web/packages/bot/src/commands/manifest.ts src/Web/packages/bot/src/index.ts
git commit -m "feat(bot): add Discord slash command manifest"
```

---

## Task 9: Write the Discord command registration script

**Files:**
- Create: `src/Web/packages/bot/src/scripts/register-discord-commands.ts`
- Modify: `src/Web/packages/bot/package.json` (add `bot:register-discord-commands` script)

**Step 1: Write the script**

```ts
// src/Web/packages/bot/src/scripts/register-discord-commands.ts
/**
 * Bulk-overwrites the set of global Discord application commands with
 * DISCORD_COMMAND_MANIFEST. Run manually after changing the manifest:
 *
 *   DISCORD_APPLICATION_ID=... DISCORD_BOT_TOKEN=... \
 *     pnpm --filter @nocturne/bot bot:register-discord-commands
 *
 * Do NOT call this from app startup — Discord imposes a 200/day/guild rate
 * limit on command creates, and global propagation is handled by Discord via
 * read-repair so instant registration isn't needed. Manual invocation as a
 * deploy step is the intended lifecycle.
 */
import { DISCORD_COMMAND_MANIFEST } from "../commands/manifest.js";

const DISCORD_API = "https://discord.com/api/v10";

async function main() {
  const applicationId = process.env.DISCORD_APPLICATION_ID;
  const botToken = process.env.DISCORD_BOT_TOKEN;

  if (!applicationId) {
    console.error("DISCORD_APPLICATION_ID env var is required");
    process.exit(1);
  }
  if (!botToken) {
    console.error("DISCORD_BOT_TOKEN env var is required");
    process.exit(1);
  }

  const url = `${DISCORD_API}/applications/${applicationId}/commands`;
  console.log(`PUT ${url}`);
  console.log(`Registering ${DISCORD_COMMAND_MANIFEST.length} commands:`);
  for (const cmd of DISCORD_COMMAND_MANIFEST) {
    console.log(`  /${cmd.name} — ${cmd.description}`);
  }

  const response = await fetch(url, {
    method: "PUT",
    headers: {
      "Authorization": `Bot ${botToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(DISCORD_COMMAND_MANIFEST),
  });

  if (!response.ok) {
    const body = await response.text();
    console.error(`Discord returned ${response.status}: ${body}`);
    process.exit(1);
  }

  const registered = (await response.json()) as Array<{ id: string; name: string }>;
  console.log(`Success. Discord now has ${registered.length} global commands:`);
  for (const cmd of registered) {
    console.log(`  ${cmd.id}  /${cmd.name}`);
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
```

**Step 2: Add pnpm script**

In `src/Web/packages/bot/package.json`, add to `scripts`:

```json
"bot:register-discord-commands": "node dist/scripts/register-discord-commands.js"
```

**Step 3: Build & smoke test (dry-run)**

```bash
cd src/Web/packages/bot && pnpm run build
# Check that the compiled script exists:
ls dist/scripts/register-discord-commands.js
```

Do NOT actually invoke the script yet — that's the manual verification step in Task 10.

**Step 4: Commit**

```bash
git add src/Web/packages/bot/src/scripts/register-discord-commands.ts \
        src/Web/packages/bot/package.json
git commit -m "feat(bot): add Discord slash command registration script"
```

---

## Task 10: End-to-end manual verification

This task is manual — no code, no commits. Confirm the wiring works before merging.

**Step 1: Full build**

```bash
pnpm --filter @nocturne/bot run build
cd src/Web/packages/app && pnpm run check
```

**Step 2: Register commands with Discord**

From the repo root:

```bash
DISCORD_APPLICATION_ID=<real-app-id> \
DISCORD_BOT_TOKEN=<real-bot-token> \
  pnpm --filter @nocturne/bot run bot:register-discord-commands
```

Expected: "Success. Discord now has 5 global commands: ... /bg /glucose /connect /disconnect /status".

**Step 3: Start Aspire and test in Discord**

```bash
aspire run
```

In a Discord server where the nocturne.run bot is installed (the same one where command registration was reported broken in the original issue):

1. Type `/` — `/bg`, `/glucose`, `/connect`, `/disconnect`, `/status` must appear in the autocomplete. If they don't, propagation hasn't happened yet (global commands can take up to an hour the first time, but Discord's read-repair handles subsequent changes). Try using `/bg` directly — if it works even without showing in autocomplete, registration succeeded.
2. Invoke `/connect`. Expect: an ephemeral message containing a `…/auth/bot/authorize?state=…` link. This does NOT require an api client, so it verifies the non-`getApi` code path.
3. Follow the link to link your Nocturne account to your Discord user.
4. Invoke `/bg`. Expect: either a glucose card (if readings exist for your linked account) or "No recent glucose readings found." Either result proves `getApi()` → `locals.apiClient` → backend call is working end-to-end.
5. Invoke `/disconnect` and `/status`. Expect: stub messages ("not yet available"). These just confirm the handlers are registered.

**Step 4: Check for errors**

Review Aspire logs for any `getApi() called outside runWithApi scope` errors, uncaught handler rejections, or 401/500 responses from the Nocturne API. There should be none.

**If anything fails:** stop, diagnose, fix, re-verify. Do NOT proceed to the cleanup task until this checklist is green.

---

## Task 11: Remove the plan document

**Files:**
- Delete: `docs/plans/2026-04-08-bot-slash-commands.md`

**Step 1: Delete**

```bash
git rm docs/plans/2026-04-08-bot-slash-commands.md
```

If the `docs/plans/` directory is now empty, leaving it is fine — git doesn't track empty dirs.

**Step 2: Commit**

```bash
git commit -m "docs: remove bot slash commands implementation plan"
```

**Step 3: Push and open PR**

```bash
git push -u origin feat/bot-slash-commands
gh pr create --title "feat(bot): wire Discord slash commands end-to-end" --body "$(cat <<'EOF'
## Summary
- Adds `AsyncLocalStorage`-backed request context in `@nocturne/bot` so command handlers can fetch a request-scoped `BotApiClient` via `getApi()` instead of receiving one at registration time.
- Calls `registerAllCommands` from the `getBot()` singleton (previously never invoked — Gap 1 from the original investigation).
- Wraps the Discord interactions webhook route in `runWithApi(buildBotApiClient(locals.apiClient), ...)` so handlers detached by the adapter's auto-defer still see the request-scoped client.
- Extracts `buildBotApiClient` from the existing dispatch endpoint to avoid duplication.
- Adds a `DISCORD_COMMAND_MANIFEST` and a manual `pnpm bot:register-discord-commands` script that bulk-PUTs the manifest to Discord (Gap 3 — the chat SDK does not auto-register).
- Ships `/bg`, `/glucose`, and `/connect`/`/disconnect`/`/status` (account commands). `/disconnect` and `/status` are stubs matching the existing behavior in `commands/account.ts`.

## Test plan
- [ ] `pnpm --filter @nocturne/bot run build` clean
- [ ] `pnpm --filter @nocturne/app run check` clean
- [ ] Registration script returns 5 registered commands
- [ ] `/connect` in Discord returns the authorize link (ephemeral)
- [ ] `/bg` in Discord returns a glucose card or "no readings" message
- [ ] Aspire logs contain no `getApi() called outside runWithApi scope` errors
EOF
)"
```

---

## DRY / YAGNI notes

- **No unit tests in this plan.** The bot package has no existing test infrastructure, and the value of mocking AsyncLocalStorage + the chat SDK is low compared to the end-to-end manual verification in Task 10. If a future task adds `vitest` to the bot package, a test for `runWithApi`/`getApi` round-tripping is a single `it()` worth writing.
- **No changes to Slack/Telegram/WhatsApp webhook routes.** They can adopt the same `runWithApi` wrapper when slash commands land on those platforms — out of scope here.
- **No new env vars invented.** Reuse whatever the project already uses for the public URL and Discord app id / bot token.
- **No changes to the dispatch endpoint behavior** — only the api-client construction was extracted. Existing alert delivery is untouched.
