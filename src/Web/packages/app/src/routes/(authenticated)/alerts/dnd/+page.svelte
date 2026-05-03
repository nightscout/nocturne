<script lang="ts">
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import {
    get as getDnd,
    update as updateDnd,
  } from "$api/generated/tenantAlertSettings.generated.remote";
  import { getProfileSummary } from "$api/generated/profiles.generated.remote";
  import type { TenantAlertSettingsResponse } from "$api-clients";

  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "$lib/components/ui/card";
  import * as Select from "$lib/components/ui/select";
  import { ArrowLeft, BellOff, Save, Loader2 } from "lucide-svelte";

  // ---- State ----
  let loading = $state(true);
  let saving = $state(false);
  let error = $state<string | null>(null);

  let dndManualActive = $state(false);
  let dndManualUntilLocal = $state<string>(""); // datetime-local string
  let dndScheduleEnabled = $state(false);
  let dndScheduleStart = $state("22:00");
  let dndScheduleEnd = $state("06:00");
  let timezone = $state<string>("UTC");

  // Common IANA timezones — picker just exposes a small set; power users can
  // type into the input directly. Keep the default suggestion list short to
  // avoid drowning the user in choice.
  const TIMEZONES = [
    "UTC",
    "America/New_York",
    "America/Chicago",
    "America/Denver",
    "America/Los_Angeles",
    "Europe/London",
    "Europe/Berlin",
    "Europe/Paris",
    "Asia/Tokyo",
    "Australia/Sydney",
  ];

  // Convert a UTC ISO string into a `datetime-local` input value (YYYY-MM-
  // DDTHH:mm) in the *browser's* local zone — keeps the form usable without
  // re-implementing tz conversion. The save path round-trips back to UTC.
  function isoToLocal(iso: string | null | undefined): string {
    if (!iso) return "";
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return "";
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  function localToIso(local: string): string | undefined {
    if (!local) return undefined;
    const d = new Date(local);
    return Number.isNaN(d.getTime()) ? undefined : d.toISOString();
  }

  function browserTimezone(): string | null {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
    } catch {
      return null;
    }
  }

  function applyResponse(
    r: TenantAlertSettingsResponse | null,
    fallbackTz: string | null,
  ): void {
    dndManualActive = r?.dndManualActive ?? false;
    dndManualUntilLocal = isoToLocal(r?.dndManualUntil);
    dndScheduleEnabled = r?.dndScheduleEnabled ?? false;
    dndScheduleStart = r?.dndScheduleStart ?? "22:00";
    dndScheduleEnd = r?.dndScheduleEnd ?? "06:00";
    timezone = r?.timezone || fallbackTz || browserTimezone() || "UTC";
  }

  async function load(): Promise<void> {
    loading = true;
    error = null;
    try {
      const [r, summary] = await Promise.all([
        getDnd(),
        getProfileSummary(undefined).catch(() => null),
      ]);
      const profileTz =
        (summary?.therapySettings?.find((ts) => ts.isDefault) ??
          summary?.therapySettings?.[0])?.timezone ?? null;
      applyResponse(r, profileTz);
    } catch (e) {
      error = e instanceof Error ? e.message : "Failed to load DND settings";
    } finally {
      loading = false;
    }
  }

  async function save(): Promise<void> {
    saving = true;
    error = null;
    try {
      const r = await updateDnd({
        dndManualActive,
        dndManualUntil: localToIso(dndManualUntilLocal),
        dndScheduleEnabled,
        dndScheduleStart: dndScheduleEnabled ? dndScheduleStart : undefined,
        dndScheduleEnd: dndScheduleEnabled ? dndScheduleEnd : undefined,
        timezone,
      });
      applyResponse(r, timezone);
    } catch (e) {
      error = e instanceof Error ? e.message : "Failed to save DND settings";
    } finally {
      saving = false;
    }
  }

  onMount(load);
</script>

<svelte:head>
  <title>Do Not Disturb · Alerts · Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-3xl p-4 lg:p-6 space-y-6">
  <div class="flex items-center justify-between gap-2">
    <div class="flex items-center gap-2">
      <Button
        type="button"
        variant="ghost"
        size="icon"
        onclick={() => goto("/alerts")}
        aria-label="Back to alerts"
      >
        <ArrowLeft class="h-4 w-4" />
      </Button>
      <div>
        <h1 class="text-2xl font-bold tracking-tight flex items-center gap-2">
          <BellOff class="h-5 w-5" /> Do Not Disturb
        </h1>
        <p class="text-sm text-muted-foreground">
          Suppress non-critical alerts. Critical-severity rules and rules opted in via "Allow through DND" still fire.
        </p>
      </div>
    </div>
    <Button onclick={save} disabled={saving || loading}>
      {#if saving}
        <Loader2 class="h-4 w-4 mr-2 animate-spin" />
      {:else}
        <Save class="h-4 w-4 mr-2" />
      {/if}
      Save
    </Button>
  </div>

  {#if error}
    <div class="rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive">{error}</div>
  {/if}

  <Card>
    <CardHeader>
      <CardTitle>Manual</CardTitle>
      <CardDescription>Toggle DND on right now, optionally with an automatic expiry.</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <div class="flex items-center justify-between">
        <Label for="dnd-manual">Do Not Disturb is currently</Label>
        <Switch
          id="dnd-manual"
          checked={dndManualActive}
          onCheckedChange={(c) => (dndManualActive = c)}
        />
      </div>
      {#if dndManualActive}
        <div class="space-y-2">
          <Label for="dnd-until">Auto-expire (optional)</Label>
          <Input
            id="dnd-until"
            type="datetime-local"
            value={dndManualUntilLocal}
            oninput={(e) => (dndManualUntilLocal = e.currentTarget.value)}
          />
          <p class="text-xs text-muted-foreground">Leave blank to keep DND on indefinitely.</p>
        </div>
      {/if}
    </CardContent>
  </Card>

  <Card>
    <CardHeader>
      <CardTitle>Schedule</CardTitle>
      <CardDescription>Recurring quiet hours. Cross-midnight windows are allowed.</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <div class="flex items-center justify-between">
        <Label for="dnd-schedule">Use a recurring quiet-hours window</Label>
        <Switch
          id="dnd-schedule"
          checked={dndScheduleEnabled}
          onCheckedChange={(c) => (dndScheduleEnabled = c)}
        />
      </div>
      {#if dndScheduleEnabled}
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <Label for="dnd-start">From</Label>
            <Input
              id="dnd-start"
              type="time"
              value={dndScheduleStart}
              oninput={(e) => (dndScheduleStart = e.currentTarget.value)}
            />
          </div>
          <div class="space-y-2">
            <Label for="dnd-end">To</Label>
            <Input
              id="dnd-end"
              type="time"
              value={dndScheduleEnd}
              oninput={(e) => (dndScheduleEnd = e.currentTarget.value)}
            />
          </div>
        </div>
        <div class="space-y-2">
          <Label>Timezone</Label>
          <Select.Root
            type="single"
            value={timezone}
            onValueChange={(v) => (timezone = v)}
          >
            <Select.Trigger>{timezone}</Select.Trigger>
            <Select.Content>
              {#each Array.from(new Set([timezone, ...TIMEZONES])).filter(Boolean) as tz (tz)}
                <Select.Item value={tz} label={tz} />
              {/each}
            </Select.Content>
          </Select.Root>
          <p class="text-xs text-muted-foreground">Falls back to UTC if the value can't be resolved at evaluation time.</p>
        </div>
      {/if}
    </CardContent>
  </Card>
</div>
