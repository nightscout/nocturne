<script lang="ts">
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatNumericDate } from "#lib/utils/formatting.js";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import ClockIcon from "@lucide/svelte/icons/clock";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { toast } from "svelte-sonner";
  import { useToastSubmission } from "#lib/forms/index.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import {
    list as listClockFaces,
    create as createClockFace,
    remove as removeClockFace,
  } from "#lib/api/generated/clockFaces.generated.remote.js";
  import ClockFacePreview from "#lib/components/clock/ClockFacePreview.svelte";

  const clockFacesQuery = listClockFaces();
  const canManage = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "sharing.manage"),
  );

  let creating = $state(false);
  const deletion = useToastSubmission("Failed to delete clock face");
  let deleteDialogOpen = $state(false);
  let clockFaceToDelete = $state<{ id: string; name: string } | null>(null);

  async function handleCreate() {
    creating = true;
    try {
      const result = await createClockFace({ name: "New clock face" });
      if (result.id) {
        goto(resolve("/(authenticated)/clock/config/[id]", { id: result.id }));
      } else {
        toast.error("Failed to create clock face");
      }
    } catch (err) {
      console.error("Failed to create clock face:", err);
      toast.error(describeSubmitError(err, "Failed to create clock face"));
    } finally {
      creating = false;
    }
  }

  function openDeleteDialog(id: string, name: string) {
    clockFaceToDelete = { id, name };
    deleteDialogOpen = true;
  }

  async function confirmDelete() {
    const target = clockFaceToDelete;
    if (!target) return;

    await deletion.run(async () => {
      await removeClockFace(target.id);
      toast.success("Clock face deleted");
      deleteDialogOpen = false;
      clockFaceToDelete = null;
    });
  }
</script>

<svelte:head>
  <title>Clock faces - Nocturne</title>
</svelte:head>

<div class="@container min-h-dvh overflow-y-auto bg-background p-4 text-foreground @md:p-6 @3xl:p-8">
  <div class="mx-auto max-w-4xl">
    <div class="mb-8 flex flex-wrap items-center justify-between gap-4">
      <div>
        <h1 class="text-2xl font-bold @lg:text-3xl">Clock faces</h1>
        <p class="mt-1 text-muted-foreground">
          {canManage ? "Create and manage your custom clock displays" : "Your clock displays"}
        </p>
      </div>
      {#if canManage}
        <Button onclick={handleCreate} disabled={creating}>
          {#if creating}
            <Loader2 class="size-4 animate-spin" />
          {:else}
            <Plus class="size-4" />
          {/if}
          New clock
        </Button>
      {/if}
    </div>

    <svelte:boundary>
      {#snippet pending()}
        <div class="flex items-center justify-center py-12">
          <ClockIcon class="size-8 animate-pulse text-muted-foreground" />
        </div>
      {/snippet}
      {#snippet failed(error, reset)}
        <Card.Root variant="destructive">
          <Card.Content class="py-8 text-center space-y-3">
            <p class="text-destructive">
              {remoteErrorMessage(error, "Failed to load clock faces")}
            </p>
            <Button variant="outline" onclick={reset}>Retry</Button>
          </Card.Content>
        </Card.Root>
      {/snippet}

      {@const clockFaces = await clockFacesQuery ?? []}

      {#if clockFaces.length === 0 && canManage}
        <EmptyState
          art="clock"
          variant="dashed"
          title="No clock faces yet"
          headingLevel={3}
          body="Create your first custom clock face to display your glucose data exactly how you want it."
        >
          {#snippet action()}
            <Button onclick={handleCreate} disabled={creating}>
              {#if creating}
                <Loader2 class="size-4 animate-spin" />
              {:else}
                <Plus class="size-4" />
              {/if}
              Create clock face
            </Button>
          {/snippet}
        </EmptyState>
      {:else if clockFaces.length === 0}
        <EmptyState
          art="clock"
          variant="dashed"
          title="No clock faces yet"
          headingLevel={3}
        />
      {:else}
        <!-- Clock Face Grid -->
        <div class="grid gap-4 @lg:grid-cols-2 @3xl:grid-cols-3">
          {#each clockFaces as face (face.id)}
            <Card.Root interactive class="group cursor-pointer">
              <!-- Each preview reads its own query's state rather than awaiting it: an await here
                   is work the enclosing boundary has to finish before it can show the list at all,
                   and one preview that never resolves holds the whole page on its placeholder. -->
              <div class="h-32 overflow-hidden">
                <ClockFacePreview faceId={face.id} />
              </div>

              <Card.Content class="p-4">
                <div class="flex items-start justify-between">
                  <div>
                    <Card.Title class="font-semibold">{face.name}</Card.Title>
                    <Card.Description size="sm">
                      {#if face.updatedAt}
                        Updated {formatNumericDate(new Date(face.updatedAt))}
                      {:else if face.createdAt}
                        Created {formatNumericDate(new Date(face.createdAt))}
                      {/if}
                    </Card.Description>
                  </div>
                  {#if canManage}
                    <Button
                      variant="ghost-destructive"
                      size="icon"
                      reveal
                      aria-label="Delete clock face"
                      onclick={(e: MouseEvent) => {
                        e.stopPropagation();
                        openDeleteDialog(face.id ?? "", face.name ?? "Untitled");
                      }}
                  >
                    <Trash2 />
                  </Button>
                  {/if}
                </div>

                <div class="mt-4 flex gap-2">
                  {#if canManage}
                    <Button
                      variant="outline"
                      size="sm"
                      class="flex-1"
                      onclick={() => goto(resolve(`clock/config/${face.id}`))}
                    >Edit</Button>
                  {/if}
                  <Button
                    size="sm"
                    class="flex-1"
                    onclick={() => goto(resolve(`clock/${face.id}`))}
                  >Open</Button>
                </div>
              </Card.Content>
            </Card.Root>
          {/each}
        </div>
      {/if}
    </svelte:boundary>
  </div>
</div>

<!-- Delete Confirmation Dialog -->
<ConfirmDialog
  bind:open={deleteDialogOpen}
  title="Delete clock face"
  confirmLabel="Delete"
  destructive
  busy={deletion.busy}
  onConfirm={confirmDelete}
>
  {#snippet description()}
    Are you sure you want to delete "{clockFaceToDelete?.name}"? This action cannot be undone.
  {/snippet}
</ConfirmDialog>
