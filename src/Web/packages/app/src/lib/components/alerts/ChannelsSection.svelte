<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as DropdownMenu from "#lib/components/ui/dropdown-menu/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import Bell from "@lucide/svelte/icons/bell";
  import X from "@lucide/svelte/icons/x";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import CalendarClock from "@lucide/svelte/icons/calendar-clock";
  import { getLinkedPlatforms } from "#lib/api/generated/linkedPlatforms.generated.remote.js";
  import { getChannelStatuses } from "#lib/api/generated/systems.generated.remote.js";
  import { getCapabilityCatalog } from "#lib/api/generated/clientDevices.generated.remote.js";
  import { getInAppRecipients, keepChannel } from "#lib/api/generated/alertRules.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { ChannelType, AlertRuleSeverity, AlertChannelState } from "@nocturne/api-client";
  import type {
    ChannelStatusEntry,
    DeviceCapabilityCatalog,
    InAppRecipientResponse,
  } from "@nocturne/api-client";
  import { EmptyState } from "#lib/components/shared/index.js";
  import DeviceChannelEditor from "./DeviceChannelEditor.svelte";
  import { applyChannelDestination, channelStateOf, type ChannelDef } from "./types";
  import { channelStateLine } from "./channelState.svelte";
  import {
    CHANNEL_META,
    findChannelMeta,
    type ChannelMetaEntry,
  } from "./channelMeta";
  import {
    channelDescription,
    channelDestinationHelper,
    channelDestinationLabel,
    channelLabel,
    destinationError,
  } from "./channelText.svelte";

  interface Props {
    channels: ChannelDef[];
    /** The rule's severity, forwarded to the device editor's hardware warning. */
    severity?: AlertRuleSeverity;
    /** Narrows the kinds that may be added to these; every kind the API offers when omitted. */
    kinds?: ChannelType[];
    /** The stored rule the channels belong to. A channel can be kept only on a stored rule. */
    ruleId?: string;
  }

  let {
    channels = $bindable(),
    severity = AlertRuleSeverity.Warning,
    kinds,
    ruleId,
  }: Props = $props();

  const linkedPlatformsQuery = getLinkedPlatforms();
  const linkedPlatforms = $derived<string[]>(
    linkedPlatformsQuery.current?.platforms ?? [],
  );

  // The API decides which kinds may be added and which of them need a destination
  // typed in; this table only says how to present them.
  const statusQuery = getChannelStatuses();
  const statuses = $derived<Map<string, ChannelStatusEntry>>(
    new Map(
      (statusQuery.current?.channels ?? []).flatMap((c): [string, ChannelStatusEntry][] => (c.channelType ? [[c.channelType, c]] : [])),
    ),
  );
  const statusPending = $derived(
    statusQuery.current == null && statusQuery.error == null,
  );
  const statusFailed = $derived(
    statusQuery.current == null && statusQuery.error != null,
  );

  const catalogQuery = getCapabilityCatalog();
  const catalog = $derived<DeviceCapabilityCatalog | null>(
    catalogQuery.current ?? null,
  );

  function isLinked(opt: ChannelMetaEntry): boolean {
    return !opt.platform || linkedPlatforms.includes(opt.platform);
  }

  function isOffered(opt: ChannelMetaEntry): boolean {
    return statuses.get(opt.type)?.offered === true;
  }

  function needsDestination(channelType: ChannelType | undefined): boolean {
    return channelType !== undefined && statuses.get(channelType)?.requiresDestination === true;
  }

  // Every entry is listed but disabled until the API has answered, so the menu never
  // silently shrinks to nothing while the query is in flight or after it fails.
  const channelOptions = $derived(
    (statusPending || statusFailed ? CHANNEL_META : CHANNEL_META.filter(isOffered)).filter(
      (o) => !kinds || kinds.includes(o.type),
    ),
  );

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

  // An in-app channel left without a recipient goes to whoever saves the rule.
  const SAVER = "saver";

  const mySubjectId = $derived(page.data.user?.subjectId);
  const canEdit = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "alerts.readwrite"),
  );
  const hasInApp = $derived(channels.some((c) => c.channelType === ChannelType.InApp));
  const recipientsQuery = $derived(canEdit && hasInApp ? getInAppRecipients() : null);
  const others = $derived<InAppRecipientResponse[]>(
    (recipientsQuery?.current ?? []).filter((r) => r.subjectId !== mySubjectId),
  );

  /** The stored recipient the API says cannot receive, while the channel still names it. */
  function cannotReceive(ch: ChannelDef): boolean {
    return (
      ch.recipient?.canReceive === false &&
      (ch.recipient.subjectId ?? "") === ch.destination
    );
  }

  /** A stored in-app channel states its recipient's state only while it still names that recipient. */
  function stateLine(ch: ChannelDef) {
    if (
      ch.channelType === ChannelType.InApp &&
      (ch.recipient?.subjectId ?? "") !== ch.destination
    ) {
      return null;
    }
    return channelStateLine(ch);
  }

  function canKeep(ch: ChannelDef): boolean {
    return (
      canEdit &&
      ruleId !== undefined &&
      ch.id !== undefined &&
      ch.state === AlertChannelState.Stopped &&
      ch.typedIn === true
    );
  }

  let keeping = $state<string | null>(null);
  let keepFailure = $state<{ id: string; message: string } | null>(null);

  async function keep(ch: ChannelDef): Promise<void> {
    if (!ruleId || !ch.id) return;
    const id = ch.id;
    keeping = id;
    keepFailure = null;
    try {
      const kept = await keepChannel({ ruleId, channelId: id });
      const stored = kept.channels?.find((c) => c.id === id);
      if (stored) Object.assign(ch, channelStateOf(stored));
    } catch (err) {
      keepFailure = { id, message: describeSubmitError(err, "Could not keep this channel. Try again.") };
    } finally {
      keeping = null;
    }
  }

  function recipientValue(ch: ChannelDef): string {
    return !ch.destination || ch.destination === mySubjectId ? SAVER : ch.destination;
  }

  function recipientLabel(ch: ChannelDef): string {
    if (cannotReceive(ch)) return ch.destination ? "Unavailable member" : "No recipient";
    if (recipientValue(ch) === SAVER) return "Me";
    return (
      others.find((r) => r.subjectId === ch.destination)?.name ??
      ch.recipient?.name ??
      "Member"
    );
  }

  function removeChannel(index: number): void {
    channels.splice(index, 1);
  }

  function platformLabel(platform: string): string {
    if (platform === "whatsapp") return "WhatsApp";
    return platform.charAt(0).toUpperCase() + platform.slice(1);
  }

  /** WhatsApp has no account page: a member links it by sending a command to the bot. */
  function linkInstruction(platform: string): string {
    return platform === "whatsapp"
      ? "Send /connect to the Nocturne WhatsApp number to enable delivery."
      : "Connect it in Connectors & apps to enable delivery.";
  }
</script>

<div class="space-y-2">
  {#if channels.length === 0}
    <EmptyState
      art="chat-bubble"
      size="compact"
      title="No channels configured"
      body="Add at least one to receive this alert."
    />
  {/if}

  {#each channels as ch, i (ch._uid ?? i)}
    {@const opt = findChannelMeta(ch.channelType)}
    {@const Glyph = opt?.icon ?? Bell}
    {@const linked = !opt || isLinked(opt)}
    {@const line = stateLine(ch)}
    <div
      class="flex items-start gap-2 rounded-md border bg-background p-3"
      role="group"
      aria-labelledby="channel-name-{i}"
      aria-describedby={line ? `channel-state-${i}` : undefined}
    >
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
            <div id="channel-name-{i}" class="text-sm font-medium">{opt ? channelLabel(opt.type) : ch.channelType}</div>
            {#if opt}
              <div class="text-xs text-muted-foreground">{channelDescription(opt.type)}</div>
            {/if}
            {#if !linked && opt?.platform && ch.state === undefined}
              <div class="text-xs text-status-warning mt-0.5">
                {platformLabel(opt.platform)} not linked. Nothing is delivered until it is connected.
              </div>
            {/if}
            {#if line}
              <p
                id="channel-state-{i}"
                class="mt-0.5 flex items-start gap-1.5 text-xs {line.tone === 'warn'
                  ? 'text-status-warning'
                  : 'text-muted-foreground'}"
                data-testid="channel-state"
              >
                {#if line.tone === "warn"}
                  <TriangleAlert class="mt-0.5 h-3 w-3 shrink-0" aria-hidden="true" />
                {:else}
                  <CalendarClock class="mt-0.5 h-3 w-3 shrink-0" aria-hidden="true" />
                {/if}
                <span>{line.text}</span>
              </p>
            {/if}
            {#if canKeep(ch)}
              <Button
                type="button"
                variant="outline"
                size="xs"
                class="mt-1.5"
                disabled={keeping === ch.id}
                onclick={() => keep(ch)}
              >
                Keep this channel
              </Button>
              {@const failure = keepFailure}
              {#if failure && failure.id === ch.id}
                <p class="mt-1 text-xs text-destructive" role="alert">{failure.message}</p>
              {/if}
            {/if}
          </div>
          <Button
            type="button"
            variant="ghost"
            size="icon-xs"
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
        {:else if ch.channelType === ChannelType.InApp}
          {@const unavailable = cannotReceive(ch)}
          <div class="space-y-1.5">
            <Label size="sm" for="channel-recipient-{i}">Recipient</Label>
            <Select.Root
              type="single"
              value={unavailable ? "" : recipientValue(ch)}
              onValueChange={(v) => {
                channels[i].destination = v === SAVER ? "" : v;
              }}
            >
              <Select.Trigger
                id="channel-recipient-{i}"
                size="sm"
                class="w-56"
                data-testid="in-app-recipient-trigger"
              >
                {recipientLabel(ch)}
              </Select.Trigger>
              <Select.Content>
                <Select.Item value={SAVER} label="Me" />
                {#each others as r (r.subjectId)}
                  <Select.Item value={r.subjectId ?? ""} label={r.name ?? ""} />
                {/each}
              </Select.Content>
            </Select.Root>
            {#if unavailable}
              <p class="text-xs text-status-warning">
                Saving the rule sends this channel to you.
              </p>
            {:else}
              <p class="text-xs text-muted-foreground">
                Shown in this person's Nocturne notification center.
              </p>
            {/if}
          </div>
        {:else}
          {#if opt && needsDestination(ch.channelType)}
            {@const error = destinationError(opt, ch.destination)}
            {@const helper = channelDestinationHelper(opt.type)}
            <div class="space-y-1.5">
              <Label size="sm" for="channel-dest-{i}">{channelDestinationLabel(opt.type)}</Label>
              <Input
                id="channel-dest-{i}"
                type="text"
                size="sm"
                placeholder={opt.destinationPlaceholder}
                aria-invalid={error != null}
                aria-describedby={error != null
                  ? `channel-dest-error-${i}`
                  : helper
                    ? `channel-dest-helper-${i}`
                    : undefined}
                value={ch.destination}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                  applyChannelDestination(channels[i], e.currentTarget.value);
                }}
              />
              {#if error}
                <p id="channel-dest-error-{i}" class="text-xs text-status-warning">
                  {error}
                </p>
              {:else if helper}
                <p id="channel-dest-helper-{i}" class="text-xs text-muted-foreground">
                  {helper}
                </p>
              {/if}
            </div>
          {:else if opt && channelDestinationHelper(opt.type) && ch.state === undefined}
            <p class="text-xs text-muted-foreground">{channelDestinationHelper(opt.type)}</p>
          {/if}
          {#if ch.channelType === ChannelType.Webhook}
            <div class="space-y-1.5">
              <Label size="sm" for="channel-secret-{i}">Signing secret (optional)</Label>
              <div class="flex items-center gap-2">
                <Input
                  id="channel-secret-{i}"
                  type="password"
                  autocomplete="off"
                  size="sm"
                  placeholder={ch.hasSecret ? "Saved. Type to replace" : "Shared with your receiver"}
                  aria-describedby="channel-secret-helper-{i}"
                  value={ch.secret ?? ""}
                  oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                    channels[i].secret = e.currentTarget.value;
                  }}
                />
                {#if ch.hasSecret}
                  <Button
                    type="button"
                    variant="ghost"
                    size="xs"
                    class="shrink-0"
                    onclick={() => {
                      channels[i].hasSecret = false;
                      channels[i].secret = "";
                    }}
                  >
                    Remove
                  </Button>
                {/if}
              </div>
              <p id="channel-secret-helper-{i}" class="text-xs text-muted-foreground">
                Set this to have Nocturne sign each request with an
                <code>X-Nocturne-Signature</code> header your receiver can verify. Leave blank if
                your receiver does not check signatures.{#if ch.hasSecret}
                  Changing the URL above does not carry the saved secret over. Enter it again.
                {/if}
              </p>
            </div>
          {/if}
          <div class="space-y-1.5">
            <Label size="sm" for="channel-label-{i}">Label (optional)</Label>
            <Input
              id="channel-label-{i}"
              type="text"
              size="sm"
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

  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        <Button
          {...props}
          type="button"
          variant="dashed"
          size="sm"
        >
          <Plus class="h-4 w-4 mr-2" /> Add channel
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content class="w-80 max-h-96" align="start">
      {#each channelOptions as o (o.type)}
        {@const Glyph = o.icon ?? Bell}
        {@const linked = isLinked(o)}
        {@const catalogFailed =
          o.isDeviceAction === true &&
          catalog === null &&
          catalogQuery.error != null}
        {@const catalogPending =
          o.isDeviceAction === true && catalog === null && !catalogFailed}
        {@const pending = statusPending || catalogPending}
        {@const unavailable = statusFailed || catalogFailed}
        <DropdownMenu.Item
          class="items-start"
          disabled={pending || unavailable}
          title={linked
            ? undefined
            : `${platformLabel(o.platform!)} not linked. ${linkInstruction(o.platform!)}`}
          onSelect={() => addChannel(o)}
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
              <span class="text-sm font-medium">{channelLabel(o.type)}</span>
              {#if pending}
                <span class="rounded bg-muted px-1.5 py-0.5 text-2xs uppercase tracking-wide text-muted-foreground">
                  Loading
                </span>
              {:else if unavailable}
                <span class="rounded bg-muted px-1.5 py-0.5 text-2xs uppercase tracking-wide text-muted-foreground">
                  Unavailable
                </span>
              {:else if !linked}
                <span class="rounded bg-muted px-1.5 py-0.5 text-2xs uppercase tracking-wide text-muted-foreground">
                  Not linked
                </span>
              {/if}
            </span>
            <span class="text-xs text-muted-foreground leading-tight">{channelDescription(o.type)}</span>
          </span>
        </DropdownMenu.Item>
      {/each}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
</div>
