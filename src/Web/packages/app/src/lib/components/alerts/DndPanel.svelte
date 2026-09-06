<script lang="ts">
  import { formatLocale } from "$lib/utils/formatting";
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import {
    get as getDnd,
    update as updateDnd,
  } from "$api/generated/tenantAlertSettings.generated.remote";
  import type { TenantAlertSettingsResponse } from "$api-clients";
  import { describeSubmitError } from "$lib/forms";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { Bell, BellOff, Settings as SettingsIcon, Loader2 } from "lucide-svelte";
  import { isDndActiveNow, isDndScheduleConfigured } from "./dnd";

  interface Props {
    /** Called after navigating away (e.g. to close a parent popover). */
    onNavigate?: () => void;
  }

  const { onNavigate }: Props = $props();

  const effectivePermissions: string[] = $derived(
    (page.data as any).effectivePermissions ?? [],
  );
  // Manual DND is tenant-wide — it suppresses delivery of every non-critical
  // alert for every member — so the server gates it on alerts.readwrite.
  const canSetDnd = $derived(
    effectivePermissions.includes("*") ||
      effectivePermissions.includes("alerts.readwrite"),
  );

  let settings = $state<TenantAlertSettingsResponse | null>(null);
  let loading = $state(true);
  let saving = $state(false);
  let expanded = $state(false);
  let errorMessage = $state<string | null>(null);

  async function load(): Promise<void> {
    try {
      settings = await getDnd().run();
    } catch (err) {
      settings = null;
      errorMessage = remoteErrorMessage(err, "Couldn't load Do Not Disturb.");
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
      settings = r;
      expanded = false;
    } catch (e) {
      errorMessage = describeSubmitError(
        e,
        "Couldn't change Do Not Disturb. Please try again.",
      );
    } finally {
      saving = false;
    }
  }

  // `.run()` rejects during the render flush, so defer the bootstrap to a microtask.
  onMount(() => {
    if (canSetDnd) queueMicrotask(load);
  });

  // Only the manual mute is knowable as "on now"; a configured schedule reads as
  // "Scheduled" without muting the bell. See lib/components/alerts/dnd.ts.
  let isActive = $derived(isDndActiveNow(settings));
  let label = $derived(
    settings?.dndManualActive
      ? settings.dndManualUntil
        ? `Until ${new Date(settings.dndManualUntil).toLocaleTimeString(formatLocale(), { hour: "numeric", minute: "2-digit" })}`
        : "On"
      : isDndScheduleConfigured(settings)
        ? "Scheduled"
        : "Off",
  );
</script>

{#if canSetDnd}
  <div class="border-b px-2 py-2">
    <button
      type="button"
      class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-muted {isActive ? 'text-status-info' : ''}"
      onclick={() => (expanded = !expanded)}
      aria-expanded={expanded}
    >
      {#if isActive}
        <BellOff class="h-4 w-4" />
      {:else}
        <Bell class="h-4 w-4" />
      {/if}
      <span class="flex-1 text-left truncate">Do Not Disturb</span>
      <span class="text-xs text-muted-foreground">{loading ? "…" : label}</span>
    </button>

    {#if expanded}
      <div class="mt-1 rounded border bg-muted/30 p-1">
        {#if loading}
          <div class="px-2 py-1.5 text-sm text-muted-foreground">Loading…</div>
        {:else if isActive}
          <button
            type="button"
            class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-muted"
            onclick={() => setActive(false)}
            disabled={saving}
          >
            {#if saving}<Loader2 class="h-3.5 w-3.5 animate-spin" />{:else}<Bell class="h-3.5 w-3.5" />{/if}
            Turn off
          </button>
        {:else}
          <div class="px-2 pt-1 pb-1 text-[10px] uppercase tracking-wider text-muted-foreground">
            Mute alerts for
          </div>
          {#each [30, 60, 120, 240] as mins (mins)}
            <button
              type="button"
              class="flex w-full items-center justify-between rounded px-2 py-1.5 text-sm hover:bg-muted"
              onclick={() => setActive(true, mins)}
              disabled={saving}
            >
              <span>{mins < 60 ? `${mins} minutes` : `${mins / 60} hour${mins > 60 ? "s" : ""}`}</span>
            </button>
          {/each}
          <button
            type="button"
            class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-muted"
            onclick={() => setActive(true)}
            disabled={saving}
          >
            Until I turn it off
          </button>
        {/if}
        {#if errorMessage}
          <p class="px-2 py-1.5 text-sm text-destructive">{errorMessage}</p>
        {/if}
        <div class="my-1 border-t"></div>
        <button
          type="button"
          class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-muted"
          onclick={() => {
            onNavigate?.();
            goto("/alerts/dnd");
          }}
        >
          <SettingsIcon class="h-3.5 w-3.5" /> Configure…
        </button>
      </div>
    {/if}
  </div>
{/if}
