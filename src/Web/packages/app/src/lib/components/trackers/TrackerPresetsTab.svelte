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
  import Plus from "@lucide/svelte/icons/plus";
  import Play from "@lucide/svelte/icons/play";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { EmptyState } from "$lib/components/shared";
  import type { TrackerDefinitionDto, TrackerPresetDto } from "$api";

  interface Props {
    definitions: TrackerDefinitionDto[];
    presets: TrackerPresetDto[];
    openNewPreset: () => void;
    applyPresetHandler: (id: string) => void;
    openDeletePresetDialog: (id: string) => void;
  }

  let {
    definitions,
    presets,
    openNewPreset,
    applyPresetHandler,
    openDeletePresetDialog,
  }: Props = $props();
</script>

<Tabs.Content value="presets">
  <Card>
    <CardHeader class="flex flex-row items-center justify-between">
      <div>
        <CardTitle>Quick Presets</CardTitle>
        <CardDescription>One-click tracker activation</CardDescription>
      </div>
      {#if definitions.length > 0}
        <Button onclick={openNewPreset}>
          <Plus class="h-4 w-4 mr-2" />
          New Preset
        </Button>
      {/if}
    </CardHeader>
    <CardContent>
      {#if presets.length === 0}
        <EmptyState
          art="stopwatch"
          variant="outline"
          title="No presets yet"
          body="Create presets for one-click tracker activation"
          action={definitions.length > 0 ? createPresetAction : undefined}
        />
        {#snippet createPresetAction()}
          <Button variant="outline" onclick={openNewPreset}>
            <Plus class="h-4 w-4 mr-2" />
            Create Preset
          </Button>
        {/snippet}
      {:else}
        <div class="space-y-3">
          {#each presets as preset (preset.id)}
            <div
              class="flex items-center justify-between p-4 rounded-lg border"
            >
              <div class="flex-1">
                <div class="font-medium">{preset.name}</div>
                <div class="text-sm text-muted-foreground">
                  {preset.definitionName}
                  {#if preset.defaultStartNotes}
                    · {preset.defaultStartNotes}
                  {/if}
                </div>
              </div>
              <div class="flex items-center gap-2">
                <Button
                  variant="default"
                  size="sm"
                  onclick={() => applyPresetHandler(preset.id!)}
                >
                  <Play class="h-4 w-4 mr-1" />
                  Apply
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  onclick={() => openDeletePresetDialog(preset.id!)}
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