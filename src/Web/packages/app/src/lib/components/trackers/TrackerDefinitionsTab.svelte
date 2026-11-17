<script lang="ts">
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import Play from "@lucide/svelte/icons/play";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { TrackerCategoryIcon } from "#lib/components/icons/index.js";
  import { cn } from "#lib/utils/index.js";
  import { TrackerCategory, type TrackerDefinitionDto } from "#lib/api/index.js";

  interface Props {
    definitions: TrackerDefinitionDto[];
    categoryLabels: Record<TrackerCategory, string>;
    categoryColors: Record<TrackerCategory, string>;
    openNewDefinition: () => void;
    openStartDialog: (def: TrackerDefinitionDto) => void;
    openEditDefinition: (def: TrackerDefinitionDto) => void;
    openDeleteDefinitionDialog: (id: string) => void;
  }

  let {
    definitions,
    categoryLabels,
    categoryColors,
    openNewDefinition,
    openStartDialog,
    openEditDefinition,
    openDeleteDefinitionDialog,
  }: Props = $props();
</script>

<Tabs.Content value="definitions">
  <Card>
    <CardHeader class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between">
      <div>
        <CardTitle>Tracker definitions</CardTitle>
        <CardDescription>
          Reusable tracker templates with notification thresholds
        </CardDescription>
      </div>
      <Button size="touch" class="w-full @md:w-auto" onclick={openNewDefinition}>
        <Plus class="h-4 w-4 mr-2" />
        New definition
      </Button>
    </CardHeader>
    <CardContent>
      {#if definitions.length === 0}
        <EmptyState
          art="stopwatch"
          variant="outline"
          title="No definitions yet"
          body="Create a tracker definition to get started"
        />
      {:else}
        <div class="space-y-3">
          {#each definitions as def (def.id)}
            {@const category = def.category ?? TrackerCategory.Consumable}
            <div
              class="flex flex-col gap-3 p-4 rounded-lg border @md:flex-row @md:items-center @md:justify-between"
            >
              <div class="flex min-w-0 items-center gap-3">
                <div
                  class={cn(
                    "shrink-0 p-2 rounded-lg bg-muted",
                    categoryColors[category]
                  )}
                >
                  <TrackerCategoryIcon {category} class="h-5 w-5" />
                </div>
                <div class="min-w-0">
                  <div class="font-medium flex flex-wrap items-center gap-2 break-words">
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
                  size="touch-sm"
                  class="flex-1 @md:flex-none"
                  onclick={() => openStartDialog(def)}
                >
                  <Play class="h-4 w-4 mr-1" />
                  Start
                </Button>
                <Button
                  variant="ghost"
                  size="icon-touch"
                  onclick={() => openEditDefinition(def)}
                  aria-label="Edit {def.name}"
                  data-testid="tracker-definition-edit"
                >
                  <Pencil class="h-4 w-4" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon-touch"
                  onclick={() => openDeleteDefinitionDialog(def.id!)}
                  aria-label="Delete {def.name}"
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
