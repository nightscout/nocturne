import { SlackAdapter, cardToBlockKit, cardToFallbackText } from "@chat-adapter/slack";
import { isCardElement } from "chat";
import type { AdapterPostableMessage, CardElement, RawMessage } from "chat";
import { currentAlertAccent } from "../lib/severity.js";

const asCard = (message: AdapterPostableMessage): CardElement | undefined => {
  if (isCardElement(message)) {
    return message;
  }
  const card = typeof message === "object" && "card" in message ? message.card : undefined;
  return isCardElement(card) ? card : undefined;
};

/**
 * Slack renders a card as bare Block Kit blocks, which carry no colour; the
 * coloured bar is a property of the enclosing attachment. An accented alert is
 * therefore posted as a one-attachment message instead. `AccentedDiscordAdapter`
 * says why this is a subclass.
 *
 * This replaces `postMessage` rather than decorating it, so the accented path
 * is the stock one minus what is listed here. Anyone changing the alert card
 * should check this list first, because these gaps apply *only* to a
 * recognised severity — behaviour otherwise differs by severity on the very
 * channel the colour is for:
 *
 * - **File uploads are dropped.** Stock pulls attachments out with `extractFiles`
 *   and sends them through `uploadFiles`. `extractFiles` is internal to `chat`
 *   and not exported, so it cannot be reused here. The alert card carries no
 *   files today; the day it carries a glucose chart, this path silently loses it
 *   while an unrecognised severity uploads it. Close this before adding one.
 * - **No debug logging.** Stock logs channel, thread and block count per post.
 *
 * Mentions and error mapping are not gaps: both are reproduced below.
 */
export class AccentedSlackAdapter extends SlackAdapter {
  override async postMessage(
    threadId: string,
    message: AdapterPostableMessage,
  ): Promise<RawMessage<unknown>> {
    const accent = currentAlertAccent();
    const card = accent ? asCard(message) : undefined;
    if (!accent || !card) {
      return super.postMessage(threadId, message);
    }

    const { channel, threadTs } = this.decodeThreadId(threadId);

    try {
      const resolved = await this.resolveMessageMentions(message, threadId);
      const result = await this._client.chat.postMessage(
        await this.withToken({
          channel,
          thread_ts: threadTs || undefined,
          text: cardToFallbackText(asCard(resolved) ?? card),
          attachments: [
            { color: accent.hex, blocks: cardToBlockKit(asCard(resolved) ?? card) },
          ],
          unfurl_links: false,
          unfurl_media: false,
        }),
      );

      // `ts` is absent only on a response Slack does not document as ok; stock
      // leaves the id undefined there rather than inventing one, although
      // `RawMessage` types it as a string.
      // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- matches stock, see above
      return { id: result.ts as string, threadId, raw: result };
    } catch (error) {
      // Maps `ratelimited` onto the typed error the retry path expects; it
      // never returns.
      this.handleSlackError(error);
    }
  }
}
