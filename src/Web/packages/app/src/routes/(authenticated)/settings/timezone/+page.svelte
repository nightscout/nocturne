<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Globe, Plus, Trash2, Loader2, RefreshCw } from "lucide-svelte";
  import * as tz from "$api/generated/timezoneTimelines.generated.remote";
  import type { TimezoneTimelineEntry } from "$api";

  const timelineQuery = tz.getTimeline();
  const entries = $derived<TimezoneTimelineEntry[]>(timelineQuery.current ?? []);

  // All IANA zones for the picker, with the browser's current zone as the default.
  const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const zones: string[] =
    "supportedValuesOf" in Intl ? (Intl as typeof Intl & { supportedValuesOf(k: string): string[] }).supportedValuesOf("timeZone") : [browserZone];

  let newZone = $state(browserZone);
  let newDate = $state(""); // datetime-local "YYYY-MM-DDTHH:mm"
  let saving = $state(false);
  let errorMessage = $state<string | null>(null);

  let pendingDelete = $state<TimezoneTimelineEntry | null>(null);
  let recorrectOpen = $state(false);
  let recorrecting = $state(false);
  let recorrectMessage = $state<string | null>(null);

  // Treat the datetime-local value as a literal wall-clock (never new Date(), which would shift it by
  // the browser offset). The backend stores it as wall-clock regardless of the trailing Z.
  function toWallClockIso(local: string): string {
    const withSeconds = /T\d\d:\d\d$/.test(local) ? `${local}:00` : local;
    return `${withSeconds}Z`;
  }

  function isOrigin(e: TimezoneTimelineEntry): boolean {
    const raw = String(e.effectiveFrom ?? "");
    return raw.startsWith("0001-01-01");
  }

  function formatEffective(e: TimezoneTimelineEntry): string {
    if (isOrigin(e)) return "From the beginning";
    const raw = String(e.effectiveFrom ?? "").replace("Z", "");
    return raw.replace("T", " ").slice(0, 16);
  }

  async function addEntry() {
    if (!newZone || !newDate) return;
    saving = true;
    errorMessage = null;
    try {
      await tz.upsert({ effectiveFrom: toWallClockIso(newDate), timezone: newZone });
      newDate = "";
    } catch (e) {
      errorMessage = (e as { message?: string })?.message ?? "Failed to add entry";
    } finally {
      saving = false;
    }
  }

  async function confirmDelete() {
    const entry = pendingDelete;
    pendingDelete = null;
    if (!entry?.id) return;
    try {
      await tz.remove(entry.id);
    } catch (e) {
      errorMessage = (e as { message?: string })?.message ?? "Failed to delete entry";
    }
  }

  async function runRecorrect() {
    recorrecting = true;
    recorrectMessage = null;
    try {
      const result = await tz.recorrect({});
      recorrectMessage = result.message ?? (result.success ? "Re-import started." : "Re-import failed.");
    } catch (e) {
      recorrectMessage = (e as { message?: string })?.message ?? "Re-import failed.";
    } finally {
      recorrecting = false;
    }
  }
</script>

<div class="mx-auto max-w-2xl space-y-6 p-4">
  <div class="space-y-1">
    <h1 class="text-xl font-semibold">Timezone history</h1>
    <p class="text-muted-foreground text-sm">
      Some sources (like Glooko) record times in local clock time. Tell Nocturne where you were over time
      so those readings land at the right moment. Add an entry when you move or travel; daylight saving is
      handled automatically.
    </p>
  </div>

  <Card.Root>
    <Card.Header>
      <Card.Title>Where you've been</Card.Title>
    </Card.Header>
    <Card.Content class="space-y-4">
      {#if entries.length === 0}
        <p class="text-muted-foreground text-sm">
          No entries yet. Your home timezone is filled in automatically on the next sync.
        </p>
      {:else}
        <ul class="divide-border divide-y">
          {#each entries as entry (entry.id)}
            <li class="flex items-center justify-between gap-3 py-2">
              <div class="flex items-center gap-2">
                <Globe class="text-muted-foreground size-4 shrink-0" />
                <div>
                  <div class="text-sm font-medium">{entry.timezone}</div>
                  <div class="text-muted-foreground text-xs">{formatEffective(entry)}</div>
                </div>
              </div>
              <Button
                variant="ghost"
                size="icon"
                aria-label="Delete entry"
                onclick={() => (pendingDelete = entry)}
              >
                <Trash2 class="size-4" />
              </Button>
            </li>
          {/each}
        </ul>
      {/if}
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Add a location change</Card.Title>
    </Card.Header>
    <Card.Content class="space-y-4">
      <div class="grid gap-3 sm:grid-cols-2">
        <div class="space-y-1.5">
          <Label for="tz-zone">Location (timezone)</Label>
          <select
            id="tz-zone"
            bind:value={newZone}
            class="border-input bg-background ring-offset-background focus-visible:ring-ring h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-2 focus-visible:outline-none"
          >
            {#each zones as zone (zone)}
              <option value={zone}>{zone}</option>
            {/each}
          </select>
        </div>
        <div class="space-y-1.5">
          <Label for="tz-date">Arrived (local date & time)</Label>
          <Input id="tz-date" type="datetime-local" bind:value={newDate} />
        </div>
      </div>

      {#if errorMessage}
        <p class="text-destructive text-sm">{errorMessage}</p>
      {/if}

      <Button onclick={addEntry} disabled={saving || !newDate || !newZone}>
        {#if saving}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <Plus class="size-4" />
        {/if}
        Add entry
      </Button>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Fix already-imported data</Card.Title>
    </Card.Header>
    <Card.Content class="space-y-3">
      <p class="text-muted-foreground text-sm">
        Changing your timezone history only affects new data automatically. Re-import recent Glooko data to
        correct readings that were already saved.
      </p>
      {#if recorrectMessage}
        <p class="text-sm">{recorrectMessage}</p>
      {/if}
      <Button variant="outline" onclick={() => (recorrectOpen = true)} disabled={recorrecting}>
        {#if recorrecting}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <RefreshCw class="size-4" />
        {/if}
        Re-import recent data
      </Button>
    </Card.Content>
  </Card.Root>
</div>

<AlertDialog.Root open={pendingDelete !== null} onOpenChange={(o) => { if (!o) pendingDelete = null; }}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete this entry?</AlertDialog.Title>
      <AlertDialog.Description>
        {#if pendingDelete}
          Remove {pendingDelete.timezone} ({formatEffective(pendingDelete)}) from your timezone history.
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action onclick={confirmDelete}>Delete</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<AlertDialog.Root bind:open={recorrectOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Re-import recent data?</AlertDialog.Title>
      <AlertDialog.Description>
        This re-pulls your recent Glooko data and re-corrects timestamps using your timezone history.
        Existing readings are updated in place, not duplicated.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action onclick={runRecorrect}>Re-import</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
