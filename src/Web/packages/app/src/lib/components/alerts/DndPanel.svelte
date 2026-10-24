<script lang="ts">
  import { formatLocale } from "$lib/utils/formatting";
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import {
    get as getDnd,
    update as updateDnd,
  } from "$api/generated/tenantAlertSettings.generated.remote";
  import type { TenantAlertSettingsResponse } from "$api-clients";
  import { describeSubmitError } from "$lib/forms";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import Bell from "@lucide/svelte/icons/bell";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import SettingsIcon from "@lucide/svelte/icons/settings";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import { Item } from "$lib/components/ui/item";
  import {
    isDndInEffect,
    isDndManualActive,
    isDndScheduleActiveNow,
    isDndScheduleConfigured,
  } from "./dnd";
  import { refreshOnReturn } from "$lib/utils/refresh-on-return.svelte";

  interface Props {
    /** Called after navigating away (e.g. to close a parent popover). */
    onNavigate?: () => void;
  }

  const { onNavigate }: Props = $props();

  // Manual DND is tenant-wide: it silences every rule that is neither critical
  // nor allowed through DND, for every member. The server gates it on
  // alerts.readwrite.
  const canSetDnd = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "alerts.readwrite"),
  );

  let settings = $state<TenantAlertSettingsResponse | null>(null);
  let loading = $state(true);
  let saving = $state(false);
  let expanded = $state(false);
  let errorMessage = $state<string | null>(null);

  let reads = 0;

  async function load(): Promise<void> {
    const read = ++reads;
    try {
      const fetched = await getDnd().run();
      if (read === reads) settings = fetched;
    } catch (err) {
      if (read === reads) {
        settings = null;
        errorMessage = remoteErrorMessage(err, "Could not load Do Not Disturb.");
      }
    } finally {
      loading = false;
    }
  }

  async function setActive(active: boolean, untilMinutes?: number): Promise<void> {
    saving = true;
    errorMessage = null;
    try {
      const until =
        active && untilMinutes
          ? new Date(Date.now() + untilMinutes * 60_000).toISOString()
          : undefined;
      const r = await updateDnd({
        dndManualActive: active,
        dndManualUntil: until,
        dndScheduleEnabled: settings?.dndScheduleEnabled ?? false,
        dndScheduleStart: settings?.dndScheduleStart,
        dndScheduleEnd: settings?.dndScheduleEnd,
      });
      reads++;
      settings = r;
      expanded = false;
    } catch (e) {
      errorMessage = describeSubmitError(
        e,
        "Could not change Do Not Disturb. Try again.",
      );
    } finally {
      saving = false;
    }
  }

  // `.run()` rejects during the render flush, so defer the bootstrap to a microtask.
  onMount(() => {
    if (canSetDnd) queueMicrotask(load);
  });
  refreshOnReturn(() => {
    if (canSetDnd && !loading && !saving) void load();
  });

  let isActive = $derived(isDndManualActive(settings));
  let inEffect = $derived(isDndInEffect(settings));
  let label = $derived(
    settings?.dndManualActive
      ? settings.dndManualUntil
        ? `Until ${new Date(settings.dndManualUntil).toLocaleTimeString(formatLocale(), { hour: "numeric", minute: "2-digit" })}`
        : "On"
      : isDndScheduleActiveNow(settings)
        ? "Scheduled, on now"
        : isDndScheduleConfigured(settings)
          ? "Scheduled"
          : "Off",
  );
</script>

{#if canSetDnd}
  <div class="border-b px-2 py-2">
    <DropdownMenu.Root bind:open={expanded}>
      <DropdownMenu.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Item {...props} variant="ghost" size="sm">
            {#if inEffect}
              <BellOff class="h-4 w-4 text-status-info" />
            {:else}
              <Bell class="h-4 w-4" />
            {/if}
            <span class="flex-1 truncate text-sm {inEffect ? 'text-status-info' : ''}">Do Not Disturb</span>
            {#if saving}
              <Loader2 class="h-3.5 w-3.5 animate-spin text-muted-foreground" />
            {:else}
              <span class="text-xs text-muted-foreground">{loading ? "…" : label}</span>
            {/if}
          </Item>
        {/snippet}
      </DropdownMenu.Trigger>
      <DropdownMenu.Content class="w-76" align="start">
        {#if loading}
          <div class="px-2 py-1.5 text-sm text-muted-foreground">Loading…</div>
        {:else if isActive}
          <DropdownMenu.Item disabled={saving} onSelect={() => setActive(false)}>
            <Bell class="h-3.5 w-3.5" />
            Turn off
          </DropdownMenu.Item>
        {:else}
          <div class="px-2 pt-1 pb-1 text-2xs uppercase tracking-wider text-muted-foreground">
            Mute alerts for
          </div>
          {#each [30, 60, 120, 240] as mins (mins)}
            <DropdownMenu.Item disabled={saving} onSelect={() => setActive(true, mins)}>
              {mins < 60 ? `${mins} minutes` : `${mins / 60} hour${mins > 60 ? "s" : ""}`}
            </DropdownMenu.Item>
          {/each}
          <DropdownMenu.Item disabled={saving} onSelect={() => setActive(true)}>
            Until I turn it off
          </DropdownMenu.Item>
        {/if}
        <DropdownMenu.Separator />
        <DropdownMenu.Item
          onSelect={() => {
            onNavigate?.();
            goto(resolve("/alerts/dnd"));
          }}
        >
          <SettingsIcon class="h-3.5 w-3.5" /> Configure…
        </DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
    {#if errorMessage}
      <p class="px-2 pt-1.5 text-sm text-destructive">{errorMessage}</p>
    {/if}
  </div>
{/if}
