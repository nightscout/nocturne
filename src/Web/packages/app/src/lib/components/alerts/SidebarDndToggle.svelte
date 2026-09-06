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
  let saving = $state(false);
  let failed = $state(false);

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
      // A sidebar toggle has nowhere to put a sentence; the null state renders as
      // "unknown" and the panel behind it reports the reason.
      settings = null;
    }
  }

  async function toggleManual(checked: boolean): Promise<void> {
    if (saving) return;
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
      settings = r;
    } catch {
      // The toggle's own failed state is the report; a reason needs somewhere to
      // sit, and /alerts/dnd is where it does.
      failed = true;
    } finally {
      saving = false;
    }
  }

  // `.run()` rejects during the render flush, so defer the bootstrap to a microtask.
  onMount(() => {
    if (canSetDnd) queueMicrotask(load);
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
  {#if failed}
    <p
      class="px-2 pb-1 text-xs text-destructive group-data-[collapsible=icon]:hidden"
    >
      Couldn't change Do Not Disturb. Please try again.
    </p>
  {/if}
{/if}
