<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Popover from "$lib/components/ui/popover";
  import {
    Plus,
    Bell,
    MessageSquare,
    Send,
    Webhook,
    Smartphone,
    X,
  } from "lucide-svelte";
  import { ChannelType } from "$api-clients";
  import type { ChannelDef } from "./types";

  interface Props {
    channels: ChannelDef[];
  }

  let { channels = $bindable() }: Props = $props();

  // Display copy for each channel kind. Authoritative ChannelType values come
  // from the generated client; we surface the subset the UI lets users add.
  const CHANNEL_OPTIONS = [
    {
      type: ChannelType.WebPush,
      label: "Browser push",
      description: "Web push notification on this device",
      icon: Bell,
      destinationLabel: "",
      destinationPlaceholder: "",
      destinationRequired: false,
    },
    {
      type: ChannelType.InApp,
      label: "In-app",
      description: "Surfaces in the in-app notification tray",
      icon: Bell,
      destinationLabel: "",
      destinationPlaceholder: "",
      destinationRequired: false,
    },
    {
      type: ChannelType.Webhook,
      label: "Webhook",
      description: "POST to a custom URL",
      icon: Webhook,
      destinationLabel: "URL",
      destinationPlaceholder: "https://example.com/hook",
      destinationRequired: true,
    },
    {
      type: ChannelType.DiscordDm,
      label: "Discord DM",
      description: "Direct message via the linked Discord identity",
      icon: MessageSquare,
      destinationLabel: "",
      destinationPlaceholder: "",
      destinationRequired: false,
    },
    {
      type: ChannelType.DiscordChannel,
      label: "Discord channel",
      description: "Post to a Discord channel via webhook",
      icon: MessageSquare,
      destinationLabel: "Webhook URL",
      destinationPlaceholder: "https://discord.com/api/webhooks/…",
      destinationRequired: true,
    },
    {
      type: ChannelType.TelegramDm,
      label: "Telegram DM",
      description: "Direct message via the linked Telegram identity",
      icon: Send,
      destinationLabel: "",
      destinationPlaceholder: "",
      destinationRequired: false,
    },
    {
      type: ChannelType.SlackDm,
      label: "Slack DM",
      description: "Direct message in a Slack workspace",
      icon: MessageSquare,
      destinationLabel: "",
      destinationPlaceholder: "",
      destinationRequired: false,
    },
    {
      type: ChannelType.WhatsAppDm,
      label: "WhatsApp DM",
      description: "Direct message via WhatsApp Business",
      icon: Smartphone,
      destinationLabel: "Phone (E.164)",
      destinationPlaceholder: "+15551234567",
      destinationRequired: true,
    },
  ] as const;

  type ChannelOption = (typeof CHANNEL_OPTIONS)[number];

  function optionFor(type: ChannelType | string): ChannelOption | undefined {
    return CHANNEL_OPTIONS.find((o) => o.type === type);
  }

  function addChannel(type: ChannelType): void {
    channels.push({
      _uid:
        typeof crypto !== "undefined" && "randomUUID" in crypto
          ? crypto.randomUUID()
          : Math.random().toString(36).slice(2),
      channelType: type,
      destination: "",
      destinationLabel: "",
    });
  }

  function removeChannel(index: number): void {
    channels.splice(index, 1);
  }
</script>

<div class="space-y-2">
  {#if channels.length === 0}
    <p class="text-sm text-muted-foreground italic">
      No channels configured. Add at least one to receive this alert.
    </p>
  {/if}

  {#each channels as ch, i (ch._uid ?? i)}
    {@const opt = optionFor(ch.channelType)}
    {@const Glyph = opt?.icon ?? Bell}
    <div class="flex items-start gap-2 rounded-md border bg-background p-3">
      <span class="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
        <Glyph class="h-4 w-4" />
      </span>
      <div class="flex-1 space-y-2">
        <div class="flex items-center justify-between gap-2">
          <div>
            <div class="text-sm font-medium">{opt?.label ?? ch.channelType}</div>
            {#if opt?.description}
              <div class="text-xs text-muted-foreground">{opt.description}</div>
            {/if}
          </div>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            class="h-7 w-7"
            aria-label="Remove channel"
            onclick={() => removeChannel(i)}
          >
            <X class="h-4 w-4" />
          </Button>
        </div>
        {#if opt?.destinationRequired}
          <div class="space-y-1.5">
            <Label class="text-xs" for="channel-dest-{i}">{opt.destinationLabel}</Label>
            <Input
              id="channel-dest-{i}"
              type="text"
              class="h-8 text-sm"
              placeholder={opt.destinationPlaceholder}
              value={ch.destination}
              oninput={(e) => {
                channels[i].destination = e.currentTarget.value;
              }}
            />
          </div>
        {/if}
        <div class="space-y-1.5">
          <Label class="text-xs" for="channel-label-{i}">Label (optional)</Label>
          <Input
            id="channel-label-{i}"
            type="text"
            class="h-8 text-sm"
            placeholder="Family channel, work phone…"
            value={ch.destinationLabel ?? ""}
            oninput={(e) => {
              channels[i].destinationLabel = e.currentTarget.value;
            }}
          />
        </div>
      </div>
    </div>
  {/each}

  <Popover.Root>
    <Popover.Trigger>
      {#snippet child({ props })}
        <Button
          {...props}
          type="button"
          variant="outline"
          size="sm"
          class="border-dashed text-muted-foreground"
        >
          <Plus class="h-4 w-4 mr-2" /> Add channel
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-80 p-1" align="start">
      <div class="max-h-96 overflow-y-auto">
        {#each CHANNEL_OPTIONS as o (o.type)}
          {@const Glyph = o.icon}
          <Popover.Close>
            {#snippet child({ props })}
              <button
                {...props}
                type="button"
                class="flex w-full items-start gap-2 rounded px-2 py-1.5 text-left hover:bg-muted"
                onclick={() => addChannel(o.type)}
              >
                <span class="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
                  <Glyph class="h-3.5 w-3.5" />
                </span>
                <span class="flex flex-col">
                  <span class="text-sm font-medium">{o.label}</span>
                  <span class="text-xs text-muted-foreground leading-tight">{o.description}</span>
                </span>
              </button>
            {/snippet}
          </Popover.Close>
        {/each}
      </div>
    </Popover.Content>
  </Popover.Root>
</div>
