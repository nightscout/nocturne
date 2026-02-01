<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Slider } from "$lib/components/ui/slider";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import * as Select from "$lib/components/ui/select";
  import { X, Trash2 } from "lucide-svelte";
  import type { ClockElement, TrackerDefinitionDto } from "$lib/api/api-client";
  import {
    ELEMENT_INFO,
    VISIBILITY_OPTIONS,
    TRACKER_SHOW_OPTIONS,
    TRACKER_CATEGORIES,
    CHART_FEATURE_OPTIONS,
    type ClockElementType,
    type InternalElement,
    isTextElement,
    isShowOptionChecked,
    isCategoryChecked,
    getTrackerName,
  } from "$lib/clock-builder";
  import TextStyleControls from "./TextStyleControls.svelte";

  interface Props {
    element: InternalElement;
    trackerDefinitions: TrackerDefinitionDto[];
    onClose: () => void;
    onRemove: () => void;
    onUpdateElement: (updates: Partial<ClockElement>) => void;
    onUpdateStyle: (styleUpdates: Record<string, unknown>) => void;
    onUpdateCustomStyle: (key: string, value: string) => void;
    onRemoveCustomStyle: (key: string) => void;
  }

  let {
    element,
    trackerDefinitions,
    onClose,
    onRemove,
    onUpdateElement,
    onUpdateStyle,
    onUpdateCustomStyle,
    onRemoveCustomStyle,
  }: Props = $props();

  const info = $derived(ELEMENT_INFO[element.type as ClockElementType]);

  function toggleShowOption(currentShow: string[] | undefined, option: string) {
    const show = currentShow ?? [];
    const newShow = show.includes(option)
      ? show.filter((s) => s !== option)
      : [...show, option];
    onUpdateElement({ show: newShow });
  }

  function toggleCategory(
    currentCategories: string[] | undefined,
    category: string
  ) {
    const categories = currentCategories ?? [];
    let newCategories: string[];
    if (categories.length === 0) {
      newCategories = TRACKER_CATEGORIES.filter((c) => c !== category);
    } else if (categories.includes(category)) {
      newCategories = categories.filter((c) => c !== category);
    } else {
      newCategories = [...categories, category];
    }
    if (newCategories.length === TRACKER_CATEGORIES.length) newCategories = [];
    onUpdateElement({ categories: newCategories });
  }
</script>

<div
  class="w-80 shrink-0 overflow-y-auto overflow-x-hidden border-l bg-muted/30 p-4"
>
  <div class="flex items-center justify-between">
    <Badge>{info?.name ?? element.type}</Badge>
    <Button variant="ghost" size="icon" class="size-6" onclick={onClose}>
      <X class="size-4" />
    </Button>
  </div>
  <p class="mt-1 text-xs text-muted-foreground">
    {info?.description ?? ""}
  </p>

  <div class="mt-4 space-y-4">
    <!-- Delta options -->
    {#if element.type === "delta"}
      <div class="flex items-center justify-between">
        <Label>Show units</Label>
        <Checkbox
          checked={element.showUnits !== false}
          onCheckedChange={(v) => onUpdateElement({ showUnits: !!v })}
        />
      </div>
    {/if}

    <!-- Hours option -->
    {#if info?.hasHoursOption && element.type !== "chart"}
      <div class="space-y-2">
        <Label>Hours</Label>
        <Select.Root
          type="single"
          value={String(element.hours || 3)}
          onValueChange={(v) => onUpdateElement({ hours: parseInt(v) })}
        >
          <Select.Trigger>{element.hours || 3}h</Select.Trigger>
          <Select.Content>
            {#each [1, 3, 6, 12, 24] as h}
              <Select.Item value={String(h)}>
                {h} hour{h > 1 ? "s" : ""}
              </Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
    {/if}

    <!-- Time format option -->
    {#if info?.hasFormatOption}
      <div class="space-y-2">
        <Label>Format</Label>
        <Select.Root
          type="single"
          value={element.format || "12h"}
          onValueChange={(v) => onUpdateElement({ format: v })}
        >
          <Select.Trigger>
            {element.format === "24h" ? "24h" : "12h"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="12h">12-hour</Select.Item>
            <Select.Item value="24h">24-hour</Select.Item>
          </Select.Content>
        </Select.Root>
      </div>
    {/if}

    <!-- Minutes ahead option -->
    {#if info?.hasMinutesAheadOption}
      <div class="space-y-2">
        <Label>Minutes ahead</Label>
        <Select.Root
          type="single"
          value={String(element.minutesAhead || 30)}
          onValueChange={(v) => onUpdateElement({ minutesAhead: parseInt(v) })}
        >
          <Select.Trigger>{element.minutesAhead || 30}m</Select.Trigger>
          <Select.Content>
            {#each [15, 30, 45, 60] as m}
              <Select.Item value={String(m)}>{m} min</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
    {/if}

    <!-- Text content for text element -->
    {#if info?.hasTextOptions}
      <div class="space-y-2">
        <Label>Text</Label>
        <Textarea
          value={element.text ?? ""}
          oninput={(e) => onUpdateElement({ text: e.currentTarget.value })}
          rows={2}
          placeholder="Enter text..."
        />
      </div>
    {/if}

    <!-- Single Tracker options -->
    {#if info?.hasTrackerOptions}
      <div class="space-y-4">
        <div class="space-y-2">
          <Label>Tracker</Label>
          <Select.Root
            type="single"
            value={element.definitionId ?? ""}
            onValueChange={(v) =>
              onUpdateElement({ definitionId: v || undefined })}
          >
            <Select.Trigger>
              {element.definitionId
                ? getTrackerName(element.definitionId, trackerDefinitions)
                : "Select tracker..."}
            </Select.Trigger>
            <Select.Content>
              {#each trackerDefinitions as def}
                <Select.Item value={def.id ?? ""}>
                  {def.name}
                </Select.Item>
              {/each}
              {#if trackerDefinitions.length === 0}
                <Select.Item value="" disabled>No trackers defined</Select.Item>
              {/if}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="space-y-2">
          <Label>Visibility threshold</Label>
          <Select.Root
            type="single"
            value={element.visibilityThreshold || "always"}
            onValueChange={(v) => onUpdateElement({ visibilityThreshold: v })}
          >
            <Select.Trigger>
              {VISIBILITY_OPTIONS.find(
                (o) => o.value === (element.visibilityThreshold || "always")
              )?.label ?? "Always show"}
            </Select.Trigger>
            <Select.Content>
              {#each VISIBILITY_OPTIONS as opt}
                <Select.Item value={opt.value}>{opt.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="space-y-2">
          <Label>Show</Label>
          <div class="space-y-1">
            {#each TRACKER_SHOW_OPTIONS as opt}
              <div class="flex items-center gap-2">
                <Checkbox
                  checked={isShowOptionChecked(element.show, opt.value)}
                  onCheckedChange={() =>
                    toggleShowOption(element.show, opt.value)}
                />
                <span class="text-sm">{opt.label}</span>
              </div>
            {/each}
          </div>
        </div>
      </div>
    {/if}

    <!-- Multiple Trackers options -->
    {#if info?.hasTrackersOptions}
      <div class="space-y-4">
        <div class="space-y-2">
          <Label>Visibility threshold</Label>
          <Select.Root
            type="single"
            value={element.visibilityThreshold || "always"}
            onValueChange={(v) => onUpdateElement({ visibilityThreshold: v })}
          >
            <Select.Trigger>
              {VISIBILITY_OPTIONS.find(
                (o) => o.value === (element.visibilityThreshold || "always")
              )?.label ?? "Always show"}
            </Select.Trigger>
            <Select.Content>
              {#each VISIBILITY_OPTIONS as opt}
                <Select.Item value={opt.value}>{opt.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="space-y-2">
          <Label>Categories</Label>
          <p class="text-xs text-muted-foreground">
            {element.categories?.length
              ? `${element.categories.length} selected`
              : "All categories"}
          </p>
          <div class="grid grid-cols-2 gap-1">
            {#each TRACKER_CATEGORIES as cat}
              <div class="flex items-center gap-1">
                <Checkbox
                  checked={isCategoryChecked(element.categories, cat)}
                  onCheckedChange={() =>
                    toggleCategory(element.categories, cat)}
                />
                <span class="text-xs">{cat}</span>
              </div>
            {/each}
          </div>
        </div>
      </div>
    {/if}

    <!-- Chart element options -->
    {#if info?.hasChartOptions}
      <div class="space-y-4">
        <div class="flex items-center gap-2">
          <Checkbox
            checked={element.chartConfig?.asBackground ?? false}
            onCheckedChange={(v) =>
              onUpdateElement({
                chartConfig: {
                  ...element.chartConfig,
                  asBackground: !!v,
                },
              })}
          />
          <Label>Use as background</Label>
        </div>

        {#if !element.chartConfig?.asBackground}
          <div class="space-y-2">
            <Label>Width: {element.width || 400}px</Label>
            <Slider
              type="single"
              value={element.width || 400}
              onValueChange={(v) => onUpdateElement({ width: v })}
              min={200}
              max={800}
              step={20}
            />
          </div>
          <div class="space-y-2">
            <Label>Height: {element.height || 200}px</Label>
            <Slider
              type="single"
              value={element.height || 200}
              onValueChange={(v) => onUpdateElement({ height: v })}
              min={100}
              max={500}
              step={20}
            />
          </div>
        {/if}

        <div class="space-y-2">
          <Label>Hours</Label>
          <Select.Root
            type="single"
            value={String(element.hours || 3)}
            onValueChange={(v) => onUpdateElement({ hours: parseInt(v) })}
          >
            <Select.Trigger>{element.hours || 3}h</Select.Trigger>
            <Select.Content>
              {#each [1, 3, 6, 12, 24] as h}
                <Select.Item value={String(h)}>
                  {h} hour{h > 1 ? "s" : ""}
                </Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>

        <Separator />

        <div class="space-y-2">
          <Label>Chart Features</Label>
          <div class="space-y-1">
            {#each CHART_FEATURE_OPTIONS as { key, label, defaultValue }}
              <div class="flex items-center gap-2">
                <Checkbox
                  checked={element.chartConfig?.[
                    key as keyof typeof element.chartConfig
                  ] ?? defaultValue}
                  onCheckedChange={(v) =>
                    onUpdateElement({
                      chartConfig: {
                        ...element.chartConfig,
                        [key]: !!v,
                      },
                    })}
                />
                <span class="text-sm">{label}</span>
              </div>
            {/each}
          </div>
        </div>
      </div>
    {/if}

    <!-- Unified text style controls for text-based elements -->
    {#if isTextElement(element.type ?? "") && element.type !== "chart"}
      <TextStyleControls
        {element}
        {onUpdateStyle}
        {onUpdateElement}
        {onUpdateCustomStyle}
        {onRemoveCustomStyle}
      />
    {/if}
  </div>

  <Separator class="my-4" />

  <Button
    variant="destructive"
    size="sm"
    onclick={onRemove}
    class="w-full gap-2"
  >
    <Trash2 class="size-4" />
    Remove
  </Button>
</div>
