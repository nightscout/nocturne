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
  import Plus from "@lucide/svelte/icons/plus";
  import Play from "@lucide/svelte/icons/play";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { EmptyState } from "#lib/components/shared/index.js";
  import type { TrackerDefinitionDto, TrackerPresetDto } from "#lib/api/index.js";

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
    <CardHeader class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between">
      <div>
        <CardTitle>Quick presets</CardTitle>
        <CardDescription>One-step tracker activation</CardDescription>
      </div>
      {#if definitions.length > 0}
        <Button size="touch" class="w-full @md:w-auto" onclick={openNewPreset}>
          <Plus class="h-4 w-4 mr-2" />
          New preset
        </Button>
      {/if}
    </CardHeader>
    <CardContent>
      {#if presets.length === 0}
        <EmptyState
          art="stopwatch"
          variant="outline"
          title="No presets yet"
          body="Create presets for one-step tracker activation"
          action={definitions.length > 0 ? createPresetAction : undefined}
        />
        {#snippet createPresetAction()}
          <Button variant="outline" onclick={openNewPreset}>
            <Plus class="h-4 w-4 mr-2" />
            Create preset
          </Button>
        {/snippet}
      {:else}
        <div class="space-y-3">
          {#each presets as preset (preset.id)}
            <div
              class="flex flex-col gap-3 p-4 rounded-lg border @md:flex-row @md:items-center @md:justify-between"
            >
              <div class="min-w-0 flex-1">
                <div class="font-medium break-words">{preset.name}</div>
                <div class="text-sm text-muted-foreground break-words">
                  {preset.definitionName}
                  {#if preset.defaultStartNotes}
                    · {preset.defaultStartNotes}
                  {/if}
                </div>
              </div>
              <div class="flex items-center gap-2">
                <Button
                  variant="default"
                  size="touch-sm"
                  class="flex-1 @md:flex-none"
                  onclick={() => applyPresetHandler(preset.id!)}
                >
                  <Play class="h-4 w-4 mr-1" />
                  Apply
                </Button>
                <Button
                  variant="ghost"
                  size="icon-touch"
                  onclick={() => openDeletePresetDialog(preset.id!)}
                  aria-label="Delete {preset.name}"
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
