<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Settings2, Plus, Play, Pencil, Trash2 } from "lucide-svelte";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { cn } from "$lib/utils";
  import { TrackerCategory, type TrackerDefinitionDto } from "$api";

  let {
    definitions,
    categoryLabels,
    categoryColors,
    openNewDefinition,
    openStartDialog,
    openEditDefinition,
    openDeleteDefinitionDialog,
  } = $props<{
    definitions: TrackerDefinitionDto[];
    categoryLabels: Record<TrackerCategory, string>;
    categoryColors: Record<TrackerCategory, string>;
    openNewDefinition: () => void;
    openStartDialog: (def: TrackerDefinitionDto) => void;
    openEditDefinition: (def: TrackerDefinitionDto) => void;
    openDeleteDefinitionDialog: (id: string) => void;
  }>();
</script>

<Tabs.Content value="definitions">
  <Card>
    <CardHeader class="flex flex-row items-center justify-between">
      <div>
        <CardTitle>Tracker Definitions</CardTitle>
        <CardDescription>
          Reusable tracker templates with notification thresholds
        </CardDescription>
      </div>
      <Button onclick={openNewDefinition}>
        <Plus class="h-4 w-4 mr-2" />
        New Definition
      </Button>
    </CardHeader>
    <CardContent>
      {#if definitions.length === 0}
        <div class="text-center py-8 text-muted-foreground">
          <Settings2 class="h-12 w-12 mx-auto mb-3 opacity-50" />
          <p>No definitions yet</p>
          <p class="text-sm">
            Create a tracker definition to get started
          </p>
        </div>
      {:else}
        <div class="space-y-3">
          {#each definitions as def (def.id)}
            {@const category = def.category ?? TrackerCategory.Consumable}
            <div
              class="flex items-center justify-between p-4 rounded-lg border"
            >
              <div class="flex items-center gap-3">
                <div
                  class={cn(
                    "p-2 rounded-lg bg-muted",
                    categoryColors[category]
                  )}
                >
                  <TrackerCategoryIcon {category} class="h-5 w-5" />
                </div>
                <div>
                  <div class="font-medium flex items-center gap-2">
                    {def.name}
                    {#if def.isFavorite}
                      <Badge variant="secondary">★ Favorite</Badge>
                    {/if}
                  </div>
                  <div class="text-sm text-muted-foreground">
                    {categoryLabels[
                      def.category ?? TrackerCategory.Consumable
                    ]}
                    {#if def.lifespanHours}
                      · {def.lifespanHours}h lifespan
                    {/if}
                    {#if def.notificationThresholds && def.notificationThresholds.length > 0}
                      · {def.notificationThresholds.length} threshold{def
                        .notificationThresholds.length > 1
                        ? "s"
                        : ""}
                    {/if}
                  </div>
                </div>
              </div>
              <div class="flex items-center gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  onclick={() => openStartDialog(def)}
                >
                  <Play class="h-4 w-4 mr-1" />
                  Start
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  onclick={() => openEditDefinition(def)}
                >
                  <Pencil class="h-4 w-4" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  onclick={() => openDeleteDefinitionDialog(def.id!)}
                >
                  <Trash2 class="h-4 w-4" />
                </Button>
              </div>
            </div>
          {/each}
        </div>
      {/if}
    </CardContent>
  </Card>
</Tabs.Content>
