<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { ThresholdEditor } from "$lib/components/ui/threshold-editor";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { Link, Unlink, Plus, ChevronDown } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import type {
    NoteTrackerLink,
    NoteTrackerThreshold,
    TrackerDefinitionDto,
  } from "$api";
  import { TrackerCategory, TrackerMode } from "$api";

  interface NoteThreshold {
    id?: string;
    hoursOffset: number | undefined;
    description?: string;
  }

  interface Props {
    /** Existing tracker links from the note */
    trackerLinks?: NoteTrackerLink[];
    /** Available tracker definitions to link to */
    definitions?: TrackerDefinitionDto[];
    /** Callback when a tracker is linked */
    onLink?: (
      trackerDefinitionId: string,
      thresholds: NoteThreshold[]
    ) => Promise<void>;
    /** Callback when a tracker is unlinked */
    onUnlink?: (linkId: string) => Promise<void>;
    /** Callback when thresholds are updated for a link */
    onUpdateThresholds?: (
      linkId: string,
      thresholds: NoteThreshold[]
    ) => Promise<void>;
    /** Additional CSS classes */
    class?: string;
  }

  let {
    trackerLinks = [],
    definitions = [],
    onLink,
    onUnlink,
    onUpdateThresholds,
    class: className,
  }: Props = $props();

  // Track which links are expanded
  let expandedLinks = $state<Set<string>>(new Set());

  // Local state for thresholds being edited (to avoid flashing during API updates)
  // Key is trackerDefinitionId, value is the local thresholds array
  let localThresholds: Map<string, NoteThreshold[]> = new Map();

  // Get thresholds for a link - uses local state if available, falls back to prop
  function getThresholds(link: NoteTrackerLink): NoteThreshold[] {
    const defId = link.trackerDefinitionId ?? "";
    const local = localThresholds.get(defId);
    if (local !== undefined) {
      return local;
    }
    return toNoteThresholds(link.thresholds);
  }

  // Available definitions (not already linked)
  const availableDefinitions = $derived(
    definitions.filter(
      (d) => !trackerLinks.some((l) => l.trackerDefinitionId === d.id)
    )
  );

  // Get definition for a link
  function getDefinition(
    trackerDefinitionId: string | undefined
  ): TrackerDefinitionDto | undefined {
    return definitions.find((d) => d.id === trackerDefinitionId);
  }

  // Convert API thresholds to component format
  function toNoteThresholds(
    thresholds: NoteTrackerThreshold[] | undefined
  ): NoteThreshold[] {
    return (thresholds ?? []).map((t) => ({
      id: t.id,
      hoursOffset: t.hoursOffset,
      description: t.description,
    }));
  }

  // Toggle link expansion - uses trackerDefinitionId for stability across relinks
  function toggleExpanded(trackerDefinitionId: string) {
    if (expandedLinks.has(trackerDefinitionId)) {
      expandedLinks.delete(trackerDefinitionId);
    } else {
      expandedLinks.add(trackerDefinitionId);
    }
    expandedLinks = new Set(expandedLinks);
  }

  // Link a new tracker
  async function handleLink(definitionId: string) {
    if (!onLink) return;
    await onLink(definitionId, []);
  }

  // Unlink a tracker
  async function handleUnlink(linkId: string) {
    if (!onUnlink) return;
    await onUnlink(linkId);
  }

  // Update thresholds for a link (updates local state immediately, then syncs to API)
  async function handleThresholdsChange(
    defId: string,
    linkId: string,
    thresholds: Record<string, unknown>[]
  ) {
    // Transform to typed thresholds
    const typedThresholds: NoteThreshold[] = thresholds.map((t) => ({
      id: t.id as string | undefined,
      hoursOffset: t.hoursOffset as number | undefined,
      description: t.description as string | undefined,
    }));

    // Update local state immediately to avoid flashing
    localThresholds.set(defId, typedThresholds);

    // Then sync to API
    if (onUpdateThresholds) {
      await onUpdateThresholds(linkId, typedThresholds);
      // Clear local state after successful save so we use fresh data from props
      localThresholds.delete(defId);
    }
  }

  // Create new threshold
  function createThreshold(): NoteThreshold {
    return {
      hoursOffset: undefined,
      description: "",
    };
  }
</script>

<div class={cn("space-y-3", className)}>
  <div class="flex items-center justify-between">
    <Label class="text-sm font-medium flex items-center gap-2">
      <Link class="h-4 w-4" />
      Linked Trackers
    </Label>
    {#if availableDefinitions.length > 0}
      <Select.Root
        type="single"
        onValueChange={(v) => v && handleLink(v)}
      >
        <Select.Trigger class="w-auto gap-2">
          <Plus class="h-4 w-4" />
          Link Tracker
        </Select.Trigger>
        <Select.Content>
          {#each availableDefinitions as def}
            {@const category = def.category ?? TrackerCategory.Custom}
            <Select.Item value={def.id ?? ""}>
              <span class="flex items-center gap-2">
                <TrackerCategoryIcon {category} class="h-4 w-4" />
                {def.name}
              </span>
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    {/if}
  </div>

  {#if trackerLinks.length === 0}
    <div
      class="text-center py-4 text-muted-foreground text-sm border border-dashed rounded-lg"
    >
      <p>No linked trackers</p>
      <p class="text-xs mt-1">
        Link this note to a tracker to get reminded before appointments or due
        dates
      </p>
    </div>
  {:else}
    <div class="space-y-2">
      {#each trackerLinks as link (link.id)}
        {@const def = getDefinition(link.trackerDefinitionId)}
        {@const category = def?.category ?? TrackerCategory.Custom}
        {@const defId = link.trackerDefinitionId ?? ""}
        {@const isExpanded = expandedLinks.has(defId)}
        {@const thresholds = getThresholds(link)}
        {@const mode = def?.mode === TrackerMode.Event ? "Event" : "Duration"}

        <div class="border rounded-lg bg-muted/30">
          <div class="flex items-center justify-between p-3">
            <button
              type="button"
              class="flex items-center gap-2 flex-1 text-left"
              onclick={() => toggleExpanded(defId)}
            >
              <TrackerCategoryIcon {category} class="h-4 w-4 text-muted-foreground" />
              <span class="font-medium text-sm">{def?.name ?? "Unknown Tracker"}</span>
              {#if thresholds.length > 0}
                <span class="text-xs text-muted-foreground">
                  ({thresholds.length} reminder{thresholds.length !== 1 ? "s" : ""})
                </span>
              {/if}
              <ChevronDown
                class={cn(
                  "h-4 w-4 text-muted-foreground transition-transform ml-auto",
                  isExpanded && "rotate-180"
                )}
              />
            </button>
            <Button
              variant="ghost"
              size="icon"
              class="h-8 w-8 text-muted-foreground hover:text-destructive shrink-0"
              onclick={() => handleUnlink(link.id ?? "")}
              title="Unlink tracker"
            >
              <Unlink class="h-4 w-4" />
            </Button>
          </div>

          {#if isExpanded}
            <div class="px-3 pb-3 pt-0">
              <ThresholdEditor
                thresholds={thresholds}
                onthresholdsChange={(updated) =>
                  handleThresholdsChange(defId, link.id ?? "", updated)}
                label="Reminders"
                {mode}
                lifespanHours={def?.lifespanHours}
                {createThreshold}
                hoursField="hoursOffset"
                emptyDescription="Add reminders to get notified before the tracker is due"
              />
            </div>
          {/if}
        </div>
      {/each}
    </div>
  {/if}
</div>
