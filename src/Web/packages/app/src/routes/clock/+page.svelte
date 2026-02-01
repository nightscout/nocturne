<script lang="ts">
  import { goto } from "$app/navigation";
  import * as Card from "$lib/components/ui/card";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Button } from "$lib/components/ui/button";
  import {
    Clock as ClockIcon,
    Plus,
    Trash2,
    Loader2,
  } from "lucide-svelte";
  import { toast } from "svelte-sonner";
  import { onMount } from "svelte";
  import {
    list as listClockFaces,
    remove as removeClockFace,
    create as createClockFace,
    getById as getClockFaceById,
  } from "$lib/data/clockfaces.remote";
  import ClockFaceRenderer from "$lib/components/clock/ClockFaceRenderer.svelte";
  import type { ClockFaceListItem, ClockFaceConfig } from "$lib/api/api-client";

  // Extended type with config for preview
  interface ClockFaceWithConfig extends ClockFaceListItem {
    config?: ClockFaceConfig | null;
    configLoading?: boolean;
  }

  let clockFaces = $state<ClockFaceWithConfig[]>([]);
  let loading = $state(true);
  let creating = $state(false);
  let deleting = $state(false);
  let deleteDialogOpen = $state(false);
  let clockFaceToDelete = $state<{ id: string; name: string } | null>(null);

  // Load clock faces from API
  onMount(async () => {
    try {
      const faces = await listClockFaces();
      // Initialize with loading state for configs
      clockFaces = faces.map((face) => ({ ...face, configLoading: true }));
      loading = false;

      // Fetch configs for each clock face in parallel
      await Promise.all(
        faces.map(async (face) => {
          if (!face.id) return;
          try {
            const fullFace = await getClockFaceById(face.id);
            clockFaces = clockFaces.map((f) =>
              f.id === face.id
                ? { ...f, config: fullFace.config, configLoading: false }
                : f
            );
          } catch (err) {
            console.error(`Failed to load config for ${face.id}:`, err);
            clockFaces = clockFaces.map((f) =>
              f.id === face.id ? { ...f, configLoading: false } : f
            );
          }
        })
      );
    } catch (err) {
      console.error("Failed to load clock faces:", err);
      toast.error("Failed to load clock faces");
      loading = false;
    }
  });

  function createDefaultConfig(): ClockFaceConfig {
    return {
      rows: [
        {
          elements: [
            {
              type: "sg",
              size: 40,
              style: {
                color: "dynamic",
                font: "system",
                fontWeight: "medium",
                opacity: 1.0,
              },
            },
            {
              type: "arrow",
              size: 25,
              style: {
                color: "dynamic",
                font: "system",
                fontWeight: "medium",
                opacity: 1.0,
              },
            },
          ],
        },
        {
          elements: [
            {
              type: "delta",
              size: 14,
              showUnits: true,
              style: {
                color: "dynamic",
                font: "system",
                fontWeight: "medium",
                opacity: 1.0,
              },
            },
          ],
        },
        {
          elements: [
            {
              type: "age",
              size: 10,
              style: { font: "system", fontWeight: "medium", opacity: 0.7 },
            },
          ],
        },
      ],
      settings: {
        bgColor: false,
        staleMinutes: 13,
        alwaysShowTime: false,
        backgroundOpacity: 100,
      },
    };
  }

  async function handleCreate() {
    creating = true;
    try {
      const result = await createClockFace({
        name: "New Clock Face",
        config: createDefaultConfig(),
      });
      if (result.id) {
        goto(`/clock/config/${result.id}`);
      } else {
        toast.error("Failed to create clock face");
      }
    } catch (err) {
      console.error("Failed to create clock face:", err);
      toast.error("Failed to create clock face");
    } finally {
      creating = false;
    }
  }

  function openDeleteDialog(id: string, name: string) {
    clockFaceToDelete = { id, name };
    deleteDialogOpen = true;
  }

  async function confirmDelete() {
    if (!clockFaceToDelete) return;

    deleting = true;
    try {
      await removeClockFace(clockFaceToDelete.id);
      clockFaces = clockFaces.filter((f) => f.id !== clockFaceToDelete!.id);
      toast.success("Clock face deleted");
      deleteDialogOpen = false;
      clockFaceToDelete = null;
    } catch (err) {
      console.error("Failed to delete clock face:", err);
      toast.error("Failed to delete clock face");
    } finally {
      deleting = false;
    }
  }
</script>

<svelte:head>
  <title>Clock Faces - Nocturne</title>
</svelte:head>

<div class="min-h-dvh overflow-y-auto bg-background p-4 text-foreground sm:p-6 md:p-8">
  <div class="mx-auto max-w-4xl">
    <div class="mb-8 flex items-center justify-between">
      <div>
        <h1 class="text-2xl font-bold sm:text-3xl">Clock Faces</h1>
        <p class="mt-1 text-muted-foreground">
          Create and manage your custom clock displays
        </p>
      </div>
      <Button onclick={handleCreate} disabled={creating} class="gap-2">
        {#if creating}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <Plus class="size-4" />
        {/if}
        New Clock
      </Button>
    </div>

    {#if loading}
      <div class="flex items-center justify-center py-12">
        <ClockIcon class="size-8 animate-pulse text-muted-foreground" />
      </div>
    {:else if clockFaces.length === 0}
      <!-- Empty State -->
      <Card.Root class="border-dashed">
        <Card.Content class="flex flex-col items-center justify-center py-12">
          <div class="mb-4 rounded-full bg-muted p-4">
            <ClockIcon class="size-8 text-muted-foreground" />
          </div>
          <h3 class="mb-2 text-lg font-semibold">No clock faces yet</h3>
          <p class="mb-6 max-w-sm text-center text-muted-foreground">
            Create your first custom clock face to display your glucose data
            exactly how you want it.
          </p>
          <Button onclick={handleCreate} disabled={creating} class="gap-2">
            {#if creating}
              <Loader2 class="size-4 animate-spin" />
            {:else}
              <Plus class="size-4" />
            {/if}
            Create Clock Face
          </Button>
        </Card.Content>
      </Card.Root>
    {:else}
      <!-- Clock Face Grid -->
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {#each clockFaces as face (face.id)}
          <Card.Root
            class="group cursor-pointer transition-all hover:-translate-y-1 hover:shadow-lg"
          >
            <!-- Preview Area -->
            <div class="h-32 overflow-hidden">
              {#if face.configLoading}
                <div class="flex h-full items-center justify-center bg-neutral-950">
                  <Loader2 class="size-6 animate-spin text-muted-foreground" />
                </div>
              {:else if face.config}
                <ClockFaceRenderer
                  config={face.config}
                  scale={0.4}
                  showCharts={false}
                  class="h-full w-full"
                />
              {:else}
                <div class="flex h-full items-center justify-center bg-neutral-950">
                  <ClockIcon class="size-6 text-muted-foreground" />
                </div>
              {/if}
            </div>

            <Card.Content class="p-4">
              <div class="flex items-start justify-between">
                <div>
                  <Card.Title class="font-semibold">{face.name}</Card.Title>
                  <Card.Description class="text-xs">
                    {#if face.updatedAt}
                      Updated {new Date(face.updatedAt).toLocaleDateString()}
                    {:else if face.createdAt}
                      Created {new Date(face.createdAt).toLocaleDateString()}
                    {/if}
                  </Card.Description>
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  class="opacity-0 transition-opacity group-hover:opacity-100"
                  onclick={(e) => {
                    e.stopPropagation();
                    openDeleteDialog(face.id ?? "", face.name ?? "Untitled");
                  }}
                >
                  <Trash2 class="size-4 text-destructive" />
                </Button>
              </div>

              <div class="mt-4 flex gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  class="flex-1"
                  onclick={() => goto(`/clock/config/${face.id}`)}
                >
                  Edit
                </Button>
                <Button
                  size="sm"
                  class="flex-1"
                  onclick={() => goto(`/clock/${face.id}`)}
                >
                  Open
                </Button>
              </div>
            </Card.Content>
          </Card.Root>
        {/each}
      </div>
    {/if}
  </div>
</div>

<!-- Delete Confirmation Dialog -->
<AlertDialog.Root bind:open={deleteDialogOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete Clock Face</AlertDialog.Title>
      <AlertDialog.Description>
        Are you sure you want to delete "{clockFaceToDelete?.name}"? This action cannot be undone.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel disabled={deleting}>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={confirmDelete}
        disabled={deleting}
        class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
      >
        {#if deleting}
          <Loader2 class="mr-2 size-4 animate-spin" />
        {/if}
        Delete
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
