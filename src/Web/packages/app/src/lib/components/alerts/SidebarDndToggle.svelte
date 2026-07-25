<script lang="ts">
  import { onMount } from "svelte";
  import { page } from "$app/state";
  import {
    get as getDnd,
    update as updateDnd,
  } from "$api/generated/tenantAlertSettings.generated.remote";
  import type { TenantAlertSettingsResponse } from "$api-clients";
  import { Switch } from "$lib/components/ui/switch";
  import { Bell, BellOff } from "lucide-svelte";
  import { isDndActiveNow, isDndScheduleConfigured } from "./dnd";

  let settings = $state<TenantAlertSettingsResponse | null>(null);
  let saving = $state(false);

  // A configured schedule is not the same as DND being on now — the backend
  // evaluates the window and the response carries no "active now" field. See
  // lib/components/alerts/dnd.ts.
  let isManualActive = $derived(isDndActiveNow(settings));
  let isScheduled = $derived(isDndScheduleConfigured(settings));

  const href = "/alerts/dnd";
  let isActive = $derived(page.url.pathname.startsWith(href));

  async function load(): Promise<void> {
    try {
      settings = await getDnd().run();
    } catch {
      settings = null;
    }
  }

  async function toggleManual(checked: boolean): Promise<void> {
    if (saving) return;
    saving = true;
    try {
      const r = await updateDnd({
        dndManualActive: checked,
        dndManualUntil: undefined,
        dndScheduleEnabled: settings?.dndScheduleEnabled ?? false,
        dndScheduleStart: settings?.dndScheduleStart,
        dndScheduleEnd: settings?.dndScheduleEnd,
      });
      settings = r;
    } finally {
      saving = false;
    }
  }

  // `.run()` rejects during the render flush, so defer the bootstrap to a microtask.
  onMount(() => queueMicrotask(load));
</script>

<div
  class="text-sidebar-foreground flex h-7 min-w-0 -translate-x-px items-center gap-2 overflow-hidden rounded-md px-2 group-data-[collapsible=icon]:hidden {isActive
    ? 'bg-sidebar-accent text-sidebar-accent-foreground'
    : ''}"
  data-slot="sidebar-menu-sub-button"
>
  <a
    {href}
    class="flex flex-1 items-center gap-2 min-w-0 text-sm hover:text-sidebar-accent-foreground"
    title={isScheduled && !isManualActive
      ? "Scheduled Do Not Disturb window configured"
      : undefined}
  >
    {#if isManualActive}
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
    disabled={saving}
    aria-label="Toggle Do Not Disturb"
  />
</div>
