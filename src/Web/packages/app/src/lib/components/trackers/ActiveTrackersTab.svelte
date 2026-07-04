<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import * as Select from "$lib/components/ui/select";
  import { Plus, Timer, Check, Trash2, Droplet } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { TrackerCategory } from "$api";
  import type { NotificationUrgency, TrackerDefinitionDto, TrackerInstanceDto } from "$api";

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
  } = $props<{
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
  }>();
</script>

<Tabs.Content value="active">
  <Card>
    <CardHeader class="flex flex-row items-center justify-between">
      <div>
        <CardTitle>Active Trackers</CardTitle>
        <CardDescription>
          Currently running tracker instances
        </CardDescription>
      </div>
      {#if definitions.length > 0}
        <Select.Root type="single">
          <Select.Trigger class="w-[200px]">
            <Plus class="h-4 w-4 mr-2" />
            Start Tracker
          </Select.Trigger>
          <Select.Content>
            {#each definitions as def}
              <Select.Item
                value={def.id ?? ""}
                label={def.name ?? ""}
                onclick={() => openStartDialog(def)}
              />
            {/each}
          </Select.Content>
        </Select.Root>
      {/if}
    </CardHeader>
    <CardContent>
      {#if activeInstances.length === 0}
        <div class="text-center py-8 text-muted-foreground">
          <Timer class="h-12 w-12 mx-auto mb-3 opacity-50" />
          <p>No active trackers</p>
          <p class="text-sm">Start a tracker from your definitions</p>
        </div>
      {:else}
        <div class="space-y-3">
          {#each activeInstances as instance}
            {@const level = getInstanceLevel(instance)}
            {@const remaining = getTimeRemaining(instance)}
            <div
              class={cn(
                "flex items-center justify-between p-4 rounded-lg border",
                getLevelStyle(level)
              )}
            >
              <div class="flex items-center gap-3">
                <div
                  class={cn(
                    "text-2xl font-bold tabular-nums",
                    remaining !== undefined && remaining <= 0
                      ? "text-destructive"
                      : remaining !== undefined && remaining < 6
                        ? "text-yellow-600 dark:text-yellow-400"
                        : ""
                  )}
                >
                  {#if remaining !== undefined}
                    {remaining <= 0 ? "Overdue" : formatAge(remaining)}
                  {:else}
                    {formatAge(instance.ageHours ?? 0)}
                  {/if}
                </div>
                <div>
                  <div class="font-medium">{instance.definitionName}</div>
                  <div
                    class="text-sm text-muted-foreground flex items-center gap-1.5"
                  >
                    {formatAge(instance.ageHours ?? 0)} old · Started {formatDate(
                      instance.startedAt
                    )}
                    {#if instance.startNotes}
                      · {instance.startNotes}
                    {/if}
                  </div>
                </div>
              </div>
              <div class="flex items-center gap-2">
                {#if definitions.find((d: TrackerDefinitionDto) => d.id === instance.definitionId)?.category === TrackerCategory.Reservoir}
                  <Button
                    variant="outline"
                    size="sm"
                    onclick={openReservoirReportDialog}
                  >
                    <Droplet class="h-4 w-4 mr-1" />
                    Record Level
                  </Button>
                {/if}
                <Button
                  variant="outline"
                  size="sm"
                  onclick={() => openCompleteDialog(instance.id!)}
                >
                  <Check class="h-4 w-4 mr-1" />
                  Complete
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
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