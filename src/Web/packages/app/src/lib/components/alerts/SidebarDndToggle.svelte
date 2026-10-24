<script lang="ts">
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import {
    get as getDnd,
    update as updateDnd,
  } from "$api/generated/tenantAlertSettings.generated.remote";
  import type { TenantAlertSettingsResponse } from "$api-clients";
  import { Switch } from "$lib/components/ui/switch";
  import Bell from "@lucide/svelte/icons/bell";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import {
    isDndInEffect,
    isDndManualActive,
    isDndScheduleActiveNow,
    isDndScheduleConfigured,
  } from "./dnd";
  import { refreshOnReturn } from "$lib/utils/refresh-on-return.svelte";

  interface Props {
    /** Whether the toggle is on screen. The settings are read the first time it is. */
    visible?: boolean;
  }

  const { visible = true }: Props = $props();

  // Manual DND is tenant-wide: it silences every rule that is neither critical
  // nor allowed through DND, for every member. The server gates it on
  // alerts.readwrite.
  const canSetDnd = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "alerts.readwrite"),
  );

  let settings = $state<TenantAlertSettingsResponse | null>(null);
  let saving = $state(false);
  let failed = $state(false);

  let isManualActive = $derived(isDndManualActive(settings));
  let inEffect = $derived(isDndInEffect(settings));
  let isScheduled = $derived(isDndScheduleConfigured(settings));
  let scheduleTitle = $derived(
    isManualActive || !isScheduled
      ? undefined
      : isDndScheduleActiveNow(settings)
        ? "Scheduled Do Not Disturb window is on now"
        : "Scheduled Do Not Disturb window configured",
  );

  const href = "/alerts/dnd";
  let isActive = $derived(page.url.pathname.startsWith(href));

  let reads = 0;

  async function load(): Promise<void> {
    const read = ++reads;
    try {
      const fetched = await getDnd().run();
      if (read === reads) settings = fetched;
    } catch {
      // A sidebar toggle has nowhere to put a sentence; the null state renders as
      // "unknown" and the panel behind it reports the reason.
      if (read === reads) settings = null;
    }
  }

  // The update carries the schedule fields back, so it waits for them to be read: sent from the
  // null state it would switch off a configured schedule.
  async function toggleManual(checked: boolean): Promise<void> {
    if (saving || settings === null) return;
    saving = true;
    failed = false;
    try {
      const r = await updateDnd({
        dndManualActive: checked,
        dndManualUntil: undefined,
        dndScheduleEnabled: settings?.dndScheduleEnabled ?? false,
        dndScheduleStart: settings?.dndScheduleStart,
        dndScheduleEnd: settings?.dndScheduleEnd,
      });
      reads++;
      settings = r;
    } catch {
      // The toggle's own failed state is the report; a reason needs somewhere to
      // sit, and /alerts/dnd is where it does.
      failed = true;
    } finally {
      saving = false;
    }
  }

  let requested = false;
  refreshOnReturn(() => {
    if (visible && requested && !saving) void load();
  });
  // `.run()` rejects during the render flush, so defer the bootstrap to a microtask.
  $effect(() => {
    if (!visible || !canSetDnd || requested) return;
    requested = true;
    queueMicrotask(load);
  });
</script>

{#if canSetDnd}
  <div
    class="text-sidebar-foreground flex h-7 min-w-0 -translate-x-px items-center gap-2 overflow-hidden rounded-md px-2 group-data-[collapsible=icon]:hidden {isActive
      ? 'bg-sidebar-accent text-sidebar-accent-foreground'
      : ''}"
    data-slot="sidebar-menu-sub-button"
  >
    <a
      href={resolve(href)}
      class="flex flex-1 items-center gap-2 min-w-0 text-sm hover:text-sidebar-accent-foreground"
      title={scheduleTitle}
    >
      {#if inEffect}
        <BellOff class="size-4 shrink-0 text-status-info" />
      {:else}
        <Bell class="size-4 shrink-0" />
      {/if}
      <span class="truncate">Do Not Disturb</span>
    </a>
    <Switch
      class="scale-75 -mr-1"
      checked={isManualActive}
      onCheckedChange={toggleManual}
      disabled={saving || settings === null}
      aria-label="Toggle Do Not Disturb"
    />
  </div>
  {#if failed}
    <p
      class="px-2 pb-1 text-xs text-destructive group-data-[collapsible=icon]:hidden"
    >
      Could not change Do Not Disturb. Try again.
    </p>
  {/if}
{/if}
