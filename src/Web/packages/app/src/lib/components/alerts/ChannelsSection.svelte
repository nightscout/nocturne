<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Popover from "$lib/components/ui/popover";
  import { Plus, Bell, X } from "lucide-svelte";
  import { getLinkedPlatforms } from "$api/generated/linkedPlatforms.generated.remote";
  import { getCapabilityCatalog } from "$api/generated/clientDevices.generated.remote";
  import { ChannelType, AlertRuleSeverity } from "$api-clients";
  import type { DeviceCapabilityCatalog } from "$api-clients";
  import DeviceChannelEditor from "./DeviceChannelEditor.svelte";
  import type { ChannelDef } from "./types";
  import {
    CHANNEL_META,
    findChannelMeta,
    type ChannelMetaEntry,
  } from "./channelMeta";

  interface Props {
    channels: ChannelDef[];
    /** The rule's severity, forwarded to the device editor's hardware warning. */
    severity?: AlertRuleSeverity;
  }

  let {
    channels = $bindable(),
    severity = AlertRuleSeverity.Warning,
  }: Props = $props();

  const linkedPlatformsQuery = getLinkedPlatforms();
  const linkedPlatforms = $derived<string[]>(
    linkedPlatformsQuery.current?.platforms ?? [],
  );

  const catalogQuery = getCapabilityCatalog();
  const catalog = $derived<DeviceCapabilityCatalog | null>(
    catalogQuery.current ?? null,
  );

  function isLinked(opt: ChannelMetaEntry): boolean {
    return !opt.platform || linkedPlatforms.includes(opt.platform);
  }

  function addChannel(opt: ChannelMetaEntry): void {
    const isDevice = opt.type === ChannelType.DeviceAction;
    // Seed a device_action channel with the first catalog kind and a pre-checked
    // `notify` capability where the kind exposes it. Other channels start blank.
    const kind = isDevice ? (catalog?.kinds?.[0] ?? "") : "";
    const metadata =
      isDevice && kind
        ? {
            capabilities: (catalog?.capabilities ?? []).some(
              (c) => c.key === "notify" && (c.kinds ?? []).includes(kind),
            )
              ? ["notify"]
              : [],
          }
        : undefined;
    channels.push({
      _uid:
        typeof crypto !== "undefined" && "randomUUID" in crypto
          ? crypto.randomUUID()
          : Math.random().toString(36).slice(2),
      channelType: opt.type,
      destination: kind,
      destinationLabel: "",
      metadata,
    });
  }

  function removeChannel(index: number): void {
    channels.splice(index, 1);
  }

  function platformLabel(platform: string): string {
    return platform.charAt(0).toUpperCase() + platform.slice(1);
  }
</script>

<div class="space-y-2">
  {#if channels.length === 0}
    <p class="text-sm text-muted-foreground italic">
      No channels configured. Add at least one to receive this alert.
    </p>
  {/if}

  {#each channels as ch, i (ch._uid ?? i)}
    {@const opt = findChannelMeta(ch.channelType)}
    {@const Glyph = opt?.icon ?? Bell}
    {@const linked = !opt || isLinked(opt)}
    <div class="flex items-start gap-2 rounded-md border bg-background p-3">
      <span class="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded bg-muted text-muted-foreground overflow-hidden">
        {#if opt?.logo}
          <img src={opt.logo} alt="" class="h-5 w-5 object-contain" />
        {:else}
          <Glyph class="h-4 w-4" />
        {/if}
      </span>
      <div class="flex-1 space-y-2">
        <div class="flex items-center justify-between gap-2">
          <div>
            <div class="text-sm font-medium">{opt?.label ?? ch.channelType}</div>
            {#if opt?.description}
              <div class="text-xs text-muted-foreground">{opt.description}</div>
            {/if}
            {#if !linked && opt?.platform}
              <div class="text-xs text-status-warning mt-0.5">
                {platformLabel(opt.platform)} not linked — won't deliver until connected.
              </div>
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
        {#if opt?.isDeviceAction}
          <DeviceChannelEditor
            bind:channel={channels[i]}
            {catalog}
            {severity}
            index={i}
          />
        {:else}
          {#if opt?.destinationRequired}
            <div class="space-y-1.5">
              <Label class="text-xs" for="channel-dest-{i}">{opt.destinationLabel}</Label>
              <Input
                id="channel-dest-{i}"
                type="text"
                class="h-8 text-sm"
                placeholder={opt.destinationPlaceholder}
                value={ch.destination}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
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
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                channels[i].destinationLabel = e.currentTarget.value;
              }}
            />
          </div>
        {/if}
      </div>
    </div>
  {/each}

  <Popover.Root>
    <Popover.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
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
        {#each CHANNEL_META as o (o.type)}
          {@const Glyph = o.icon ?? Bell}
          {@const linked = isLinked(o)}
          {@const catalogFailed =
            o.isDeviceAction === true &&
            catalog === null &&
            catalogQuery.error != null}
          {@const catalogPending =
            o.isDeviceAction === true && catalog === null && !catalogFailed}
          <Popover.Close>
            {#snippet child({ props })}
              <Button
                {...props}
                type="button"
                variant="ghost"
                class="flex h-auto w-full items-start justify-start gap-2 rounded px-2 py-1.5 text-left font-normal hover:bg-muted"
                disabled={catalogPending || catalogFailed}
                title={linked
                  ? undefined
                  : `${platformLabel(o.platform!)} not linked — connect it in Connectors & Apps to enable delivery.`}
                onclick={() => addChannel(o)}
              >
                <span
                  class="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded bg-muted text-muted-foreground overflow-hidden {!linked
                    ? 'opacity-50'
                    : ''}"
                >
                  {#if o.logo}
                    <img src={o.logo} alt="" class="h-4 w-4 object-contain" />
                  {:else}
                    <Glyph class="h-3.5 w-3.5" />
                  {/if}
                </span>
                <span class="flex flex-1 flex-col {!linked ? 'opacity-60' : ''}">
                  <span class="flex items-center gap-1.5">
                    <span class="text-sm font-medium">{o.label}</span>
                    {#if catalogPending}
                      <span class="rounded bg-muted px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-muted-foreground">
                        Loading
                      </span>
                    {:else if catalogFailed}
                      <span class="rounded bg-muted px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-muted-foreground">
                        Unavailable
                      </span>
                    {:else if !linked}
                      <span class="rounded bg-muted px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-muted-foreground">
                        Not linked
                      </span>
                    {/if}
                  </span>
                  <span class="text-xs text-muted-foreground leading-tight">{o.description}</span>
                </span>
              </Button>
            {/snippet}
          </Popover.Close>
        {/each}
      </div>
    </Popover.Content>
  </Popover.Root>
</div>
