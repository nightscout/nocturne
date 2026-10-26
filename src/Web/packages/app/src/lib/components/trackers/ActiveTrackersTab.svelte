<script lang="ts">
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as DropdownMenu from "#lib/components/ui/dropdown-menu/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import Check from "@lucide/svelte/icons/check";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Droplet from "@lucide/svelte/icons/droplet";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { cn } from "#lib/utils/index.js";
  import { TrackerCategory } from "#lib/api/index.js";
  import type { NotificationUrgency, TrackerDefinitionDto, TrackerInstanceDto } from "#lib/api/index.js";

  interface Props {
    definitions: TrackerDefinitionDto[];
    activeInstances: TrackerInstanceDto[];
    openStartDialog: (def: TrackerDefinitionDto) => void;
    openCompleteDialog: (id: string) => void;
    openReservoirReportDialog: () => void;
    openDeleteInstanceDialog: (id: string) => void;
    getInstanceLevel: (instance: TrackerInstanceDto) => NotificationUrgency | null;
    getTimeRemaining: (instance: TrackerInstanceDto) => number | undefined;
    getLevelStyle: (level: NotificationUrgency | null) => string;
    formatAge: (hours: number) => string;
    formatDate: (dateStr: Date | undefined | string) => string;
  }

  let {
    definitions,
    activeInstances,
    openStartDialog,
    openCompleteDialog,
    openReservoirReportDialog,
    openDeleteInstanceDialog,
    getInstanceLevel,
    getTimeRemaining,
    getLevelStyle,
    formatAge,
    formatDate,
  }: Props = $props();
</script>

<Tabs.Content value="active">
  <Card data-testid="active-trackers">
    <CardHeader class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between">
      <div>
        <CardTitle>Active trackers</CardTitle>
        <CardDescription>
          Currently running tracker instances
        </CardDescription>
      </div>
      {#if definitions.length > 0}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button {...props} variant="outline" size="touch" class="w-full @md:w-auto">
                <Plus class="h-4 w-4 mr-2" />
                Start tracker
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="end">
            {#each definitions as def (def.id)}
              <DropdownMenu.Item onclick={() => openStartDialog(def)}>
                {def.name}
              </DropdownMenu.Item>
            {/each}
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      {/if}
    </CardHeader>
    <CardContent>
      {#if activeInstances.length === 0}
        <EmptyState
          art="stopwatch"
          title="No active trackers"
          body="Start a tracker from your definitions"
        />
      {:else}
        <div class="space-y-3">
          {#each activeInstances as instance (instance.id)}
            {@const level = getInstanceLevel(instance)}
            {@const remaining = getTimeRemaining(instance)}
            <div
              class={cn(
                "flex flex-col gap-3 p-4 rounded-lg border @md:flex-row @md:items-center @md:justify-between",
                getLevelStyle(level)
              )}
            >
              <div class="flex min-w-0 items-center gap-3">
                <div
                  class={cn(
                    "shrink-0 text-2xl font-bold tabular-nums @md:shrink",
                    remaining !== undefined && remaining <= 0
                      ? "text-destructive"
                      : remaining !== undefined && remaining < 6
                        ? "text-warning"
                        : ""
                  )}
                >
                  {#if remaining !== undefined}
                    {remaining <= 0 ? "Overdue" : formatAge(remaining)}
                  {:else}
                    {formatAge(instance.ageHours ?? 0)}
                  {/if}
                </div>
                <div class="min-w-0">
                  <div class="font-medium break-words">{instance.definitionName}</div>
                  <div class="text-sm text-muted-foreground break-words">
                    {formatAge(instance.ageHours ?? 0)} old · Started {formatDate(
                      instance.startedAt
                    )}
                    {#if instance.startNotes}
                      · {instance.startNotes}
                    {/if}
                  </div>
                </div>
              </div>
              <div class="flex flex-wrap items-center gap-2 @md:shrink-0 @md:flex-nowrap">
                {#if definitions.find((d: TrackerDefinitionDto) => d.id === instance.definitionId)?.category === TrackerCategory.Reservoir}
                  <Button
                    variant="outline"
                    size="touch-sm"
                    class="flex-1 @md:flex-none"
                    onclick={openReservoirReportDialog}
                  >
                    <Droplet class="h-4 w-4 mr-1" />
                    Record level
                  </Button>
                {/if}
                <Button
                  variant="outline"
                  size="touch-sm"
                  class="flex-1 @md:flex-none"
                  onclick={() => openCompleteDialog(instance.id!)}
                  data-testid="tracker-complete"
                >
                  <Check class="h-4 w-4 mr-1" />
                  Complete
                </Button>
                <Button
                  variant="ghost"
                  size="icon-touch"
                  onclick={() => openDeleteInstanceDialog(instance.id!)}
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
