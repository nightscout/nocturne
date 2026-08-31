<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import { Weight, Plus, Trash2, Loader2 } from "lucide-svelte";
  import * as bw from "$api/generated/bodyWeights.generated.remote";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import type { BodyWeight } from "$api";

  // create/deleteBodyWeight declare a GetBodyWeights invalidation, but it refreshes
  // getBodyWeights(undefined) — a different cache key from this subscription — so each
  // mutation below refreshes this query itself.
  const weightsQuery = bw.getBodyWeights({ count: 100, skip: 0 });
  const entries = $derived<BodyWeight[]>(weightsQuery.current ?? []);

  let newWeightKg = $state("");
  let newDate = $state(""); // datetime-local "YYYY-MM-DDTHH:mm"
  let saving = $state(false);
  let errorMessage = $state<string | null>(null);

  let pendingDelete = $state<BodyWeight | null>(null);

  function formatMills(mills: number | undefined): string {
    if (!mills) return "";
    return new Date(mills).toLocaleString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    });
  }

  async function addEntry() {
    const kg = Number(newWeightKg);
    if (!newWeightKg || Number.isNaN(kg)) return;
    saving = true;
    errorMessage = null;
    try {
      await bw.create({
        weightKg: kg,
        mills: newDate ? new Date(newDate).getTime() : Date.now(),
      });
      await weightsQuery.refresh();
      newWeightKg = "";
      newDate = "";
    } catch (e) {
      errorMessage = describeSubmitError(e, "Failed to add entry");
    } finally {
      saving = false;
    }
  }

  async function confirmDelete() {
    const entry = pendingDelete;
    pendingDelete = null;
    if (!entry?.id) return;
    try {
      await bw.deleteBodyWeight(entry.id);
      await weightsQuery.refresh();
    } catch (e) {
      errorMessage = describeSubmitError(e, "Failed to delete entry");
    }
  }
</script>

<svelte:head>
  <title>Weight History - Settings - Nocturne</title>
</svelte:head>

<div class="mx-auto max-w-2xl space-y-6 p-4">
  <div class="space-y-1">
    <h1 class="text-xl font-semibold">Weight history</h1>
    <p class="text-muted-foreground text-sm">
      Your weight over time. Each entry is dated when it was recorded — editing your current weight
      on the Patient Record page adds a new entry here rather than overwriting the last one.
    </p>
  </div>

  <Card.Root>
    <Card.Header>
      <Card.Title>Recorded weights</Card.Title>
    </Card.Header>
    <Card.Content class="space-y-4">
      {#if entries.length === 0}
        <p class="text-muted-foreground text-sm">No entries yet.</p>
      {:else}
        <ul class="divide-border divide-y">
          {#each entries as entry (entry.id)}
            <li class="flex items-center justify-between gap-3 py-2">
              <div class="flex items-center gap-2">
                <Weight class="text-muted-foreground size-4 shrink-0" />
                <div>
                  <div class="text-sm font-medium">{entry.weightKg} kg</div>
                  <div class="text-muted-foreground text-xs">{formatMills(entry.mills)}</div>
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
      <Card.Title>Add an entry</Card.Title>
    </Card.Header>
    <Card.Content class="space-y-4">
      <div class="grid gap-3 sm:grid-cols-2">
        <div class="space-y-1.5">
          <Label for="weight-kg">Weight (kg)</Label>
          <Input id="weight-kg" type="number" step="0.1" min="0" bind:value={newWeightKg} placeholder="e.g. 70" />
        </div>
        <div class="space-y-1.5">
          <Label for="weight-date">Recorded at</Label>
          <Input id="weight-date" type="datetime-local" bind:value={newDate} placeholder="Defaults to now" />
        </div>
      </div>

      {#if errorMessage}
        <p class="text-destructive text-sm">{errorMessage}</p>
      {/if}

      <Button onclick={addEntry} disabled={saving || !newWeightKg}>
        {#if saving}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <Plus class="size-4" />
        {/if}
        Add entry
      </Button>
    </Card.Content>
  </Card.Root>
</div>

<ConfirmDialog
  open={pendingDelete !== null}
  onOpenChange={(o) => { if (!o) pendingDelete = null; }}
  title="Delete this entry?"
  confirmLabel="Delete"
  onConfirm={confirmDelete}
>
  {#snippet description()}
    {#if pendingDelete}
      Remove the {pendingDelete.weightKg} kg entry from {formatMills(pendingDelete.mills)}.
    {/if}
  {/snippet}
</ConfirmDialog>
