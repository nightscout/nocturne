import { AlertChannelState, ChannelType } from "@nocturne/api-client";
import type { AlertChannelNotSent, CreateAlertRuleChannelRequest } from "@nocturne/api-client";
import { formatMediumDate } from "#lib/utils/formatting.js";
import { isStringEnumMember, type ChannelState } from "./types";
import { channelLabel } from "./channelText.svelte";
import type { ChannelKind } from "./channelMeta";

/** Whether the API says the channel does not send. A channel with no state says nothing. */
export function isNotSending(channel: Pick<ChannelState, "state">): boolean {
  return channel.state !== undefined && channel.state !== AlertChannelState.Active;
}

export interface ChannelStateLine {
  text: string;
  /** A warning for a channel that does not send; quiet for one that will stop. */
  tone: "warn" | "quiet";
}

/** The line that states a channel's state, or null when it has nothing to state. */
export function channelStateLine(channel: ChannelState): ChannelStateLine | null {
  const name = channel.memberName;
  const adder = channel.addedByName;
  const date = channel.stopsAt ? formatMediumDate(channel.stopsAt) : null;

  if (channel.state === AlertChannelState.NoRecipient) {
    return {
      tone: "warn",
      text: name
        ? `This channel has no recipient. ${name} cannot receive it.`
        : "This channel has no recipient.",
    };
  }
  if (channel.state === AlertChannelState.Stopped) {
    if (!date) return { tone: "warn", text: "This channel has stopped." };
    return {
      tone: "warn",
      text: adder
        ? `This channel stopped on ${date}. ${adder} added it.`
        : `This channel stopped on ${date}.`,
    };
  }
  if (channel.state === AlertChannelState.Active && date) {
    return {
      tone: "quiet",
      text: adder
        ? `This channel stops on ${date}. ${adder} added it.`
        : `This channel stops on ${date}.`,
    };
  }
  if (channel.state !== undefined && channel.state !== AlertChannelState.Active) {
    return { tone: "warn", text: "This channel does not send." };
  }
  return null;
}

export function channelsNotSendingMessage(count: number): string | null {
  if (count === 0) return null;
  return count === 1 ? `${count} channel is not sending.` : `${count} channels are not sending.`;
}

function isChannelKind(value: unknown): value is ChannelKind {
  return isStringEnumMember(ChannelType, value);
}

function testFireChannelName(
  notSent: AlertChannelNotSent,
  requested: Pick<CreateAlertRuleChannelRequest, "channelType" | "destinationLabel">[]
): string | null {
  const asked = notSent.index === undefined ? undefined : requested[notSent.index];
  if (asked?.destinationLabel) return asked.destinationLabel;
  const type = notSent.channelType;
  if (!isChannelKind(type)) return null;
  return `${channelLabel(type)} (channel ${(notSent.index ?? 0) + 1})`;
}

/**
 * What a test fire says of a channel it did not send, in the words of the state when it knows
 * them. `requested` is the channel list the fire went through, by position.
 */
export function channelNotSentMessage(
  notSent: AlertChannelNotSent,
  requested: Pick<CreateAlertRuleChannelRequest, "channelType" | "destinationLabel">[]
): string {
  const name = testFireChannelName(notSent, requested);
  if (!name) return "A channel did not get the test alert.";
  if (notSent.state === AlertChannelState.NoRecipient) {
    return `${name} did not get the test alert. This channel has no recipient.`;
  }
  if (notSent.state === AlertChannelState.Stopped) {
    return `${name} did not get the test alert. This channel has stopped.`;
  }
  return `${name} did not get the test alert.`;
}

function metadataText(metadata: Record<string, unknown> | undefined, key: string): string | undefined {
  const value = metadata?.[key];
  return typeof value === "string" && value !== "" ? value : undefined;
}

/**
 * The sentence for an owner's notice that a channel does not send, from the fields its metadata
 * carries. A field the metadata lacks shortens the sentence.
 */
export function channelNoticeSubtitle(metadata: Record<string, unknown> | undefined): string | null {
  const state = metadataText(metadata, "state");
  const type = metadataText(metadata, "channelType");
  const rule = metadataText(metadata, "alertRuleName");
  const stopsAt = metadataText(metadata, "stopsAt");
  const channel =
    metadataText(metadata, "destinationLabel") ??
    (isChannelKind(type) ? channelLabel(type) : "A channel");
  const date = stopsAt ? formatMediumDate(stopsAt) : null;

  if (state === AlertChannelState.NoRecipient) {
    return rule ? `${rule}: ${channel} has no recipient.` : `${channel} has no recipient.`;
  }
  if (state === AlertChannelState.Stopped) {
    if (rule && date) return `${rule}: ${channel} stopped on ${date}.`;
    if (date) return `${channel} stopped on ${date}.`;
    return rule ? `${rule}: ${channel} stopped.` : `${channel} stopped.`;
  }
  return null;
}
