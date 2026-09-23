import { DiscordAdapter } from "@chat-adapter/discord";
import { currentAlertAccent } from "../lib/severity.js";

interface EmbedBody {
  embeds: Array<Record<string, unknown>>;
}

const hasEmbeds = (body: unknown): body is EmbedBody =>
  typeof body === "object" &&
  body !== null &&
  "embeds" in body &&
  Array.isArray(body.embeds);

/**
 * `@chat-adapter/discord` fixes every embed to one brand colour and exposes no
 * per-card input for it, so an alert's severity colour is applied to the
 * outgoing embeds instead. Subclassed rather than patched in place: an edit
 * inside `node_modules` would not survive an install.
 *
 * Only posts made inside an accent scope are touched; `withAlertAccent` in
 * `../lib/severity.ts` says why the colour travels that way.
 */
export class AccentedDiscordAdapter extends DiscordAdapter {
  protected override async discordFetch(
    path: string,
    method: string,
    body?: unknown,
  ): Promise<Response> {
    const accent = currentAlertAccent();
    if (!accent || !hasEmbeds(body)) {
      return super.discordFetch(path, method, body);
    }

    return super.discordFetch(path, method, {
      ...body,
      embeds: body.embeds.map((embed) => ({ ...embed, color: accent.rgb })),
    });
  }
}
