<script lang="ts">
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Slider } from "$lib/components/ui/slider";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import { Plus, X } from "lucide-svelte";
  import {
    ELEMENT_INFO,
    FONT_OPTIONS,
    FONT_WEIGHT_OPTIONS,
    type ClockElementType,
    type InternalElement,
  } from "$lib/clock-builder";
  import type { ClockElement } from "$lib/api/api-client";

  interface Props {
    element: InternalElement;
    onUpdateStyle: (styleUpdates: Record<string, unknown>) => void;
    onUpdateElement: (updates: Partial<ClockElement>) => void;
    onUpdateCustomStyle: (key: string, value: string) => void;
    onRemoveCustomStyle: (key: string) => void;
  }

  let { element, onUpdateStyle, onUpdateElement, onUpdateCustomStyle, onRemoveCustomStyle }: Props =
    $props();
</script>

<Separator class="my-4" />
<div class="space-y-4">
  <h5 class="text-sm font-medium">Text Style</h5>

  <div class="space-y-2">
    <Label>
      Size: {element.size ||
        ELEMENT_INFO[element.type as ClockElementType]?.defaultSize ||
        20}
    </Label>
    <Slider
      type="single"
      value={element.size ||
        ELEMENT_INFO[element.type as ClockElementType]?.defaultSize ||
        20}
      onValueChange={(v) => onUpdateElement({ size: v })}
      min={ELEMENT_INFO[element.type as ClockElementType]?.minSize ?? 10}
      max={ELEMENT_INFO[element.type as ClockElementType]?.maxSize ?? 100}
      step={1}
    />
  </div>

  <div class="space-y-2">
    <Label>Font</Label>
    <Select.Root
      type="single"
      value={element.style?.font || "system"}
      onValueChange={(v) => onUpdateStyle({ font: v })}
    >
      <Select.Trigger>
        {FONT_OPTIONS.find((f) => f.value === (element.style?.font || "system"))
          ?.label ?? "System"}
      </Select.Trigger>
      <Select.Content>
        {#each FONT_OPTIONS as opt}
          <Select.Item value={opt.value}>{opt.label}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
  </div>

  <div class="space-y-2">
    <Label>Weight</Label>
    <Select.Root
      type="single"
      value={element.style?.fontWeight || "medium"}
      onValueChange={(v) => onUpdateStyle({ fontWeight: v })}
    >
      <Select.Trigger>
        {FONT_WEIGHT_OPTIONS.find(
          (f) => f.value === (element.style?.fontWeight || "medium")
        )?.label ?? "Medium"}
      </Select.Trigger>
      <Select.Content>
        {#each FONT_WEIGHT_OPTIONS as opt}
          <Select.Item value={opt.value}>{opt.label}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
  </div>

  <div class="space-y-2">
    <Label>Color</Label>
    <div class="flex items-center gap-2">
      <Checkbox
        checked={element.style?.color === "dynamic"}
        onCheckedChange={(v) =>
          onUpdateStyle({
            color: v ? "dynamic" : "#ffffff",
          })}
      />
      <span class="text-sm">Dynamic (BG-based)</span>
    </div>
    {#if element.style?.color !== "dynamic"}
      <div class="flex min-w-0 gap-2">
        <input
          type="color"
          value={element.style?.color ?? "#ffffff"}
          oninput={(e) =>
            onUpdateStyle({
              color: e.currentTarget.value,
            })}
          class="h-9 w-12 shrink-0 cursor-pointer rounded border"
        />
        <Input
          type="text"
          value={element.style?.color ?? "#ffffff"}
          oninput={(e) =>
            onUpdateStyle({
              color: e.currentTarget.value,
            })}
          placeholder="#ffffff"
          class="min-w-0 flex-1"
        />
      </div>
    {/if}
  </div>

  <div class="space-y-2">
    <Label>
      Opacity: {Math.round((element.style?.opacity ?? 1.0) * 100)}%
    </Label>
    <Slider
      type="single"
      value={Math.round((element.style?.opacity ?? 1.0) * 100)}
      onValueChange={(v) => onUpdateStyle({ opacity: v / 100 })}
      min={10}
      max={100}
      step={5}
    />
  </div>

  <!-- Custom CSS Properties -->
  <Separator />
  <div class="space-y-2">
    <Label>Custom CSS</Label>
    <p class="text-xs text-muted-foreground">
      Add custom CSS properties (e.g., text-shadow, letter-spacing)
    </p>

    {#if element.style?.custom}
      {#each Object.entries(element.style.custom) as [key, value]}
        <div class="flex min-w-0 items-center gap-2">
          <Input
            type="text"
            value={key}
            oninput={(e) => {
              const newKey = e.currentTarget.value;
              if (newKey && newKey !== key) {
                onRemoveCustomStyle(key);
                onUpdateCustomStyle(newKey, value);
              }
            }}
            placeholder="property"
            class="min-w-0 flex-1"
          />
          <Input
            type="text"
            {value}
            oninput={(e) => onUpdateCustomStyle(key, e.currentTarget.value)}
            placeholder="value"
            class="min-w-0 flex-1"
          />
          <Button
            variant="ghost"
            size="icon"
            class="shrink-0"
            onclick={() => onRemoveCustomStyle(key)}
          >
            <X class="size-4" />
          </Button>
        </div>
      {/each}
    {/if}

    <div class="flex min-w-0 gap-2">
      <Input
        type="text"
        placeholder="property"
        id="new-css-prop-{element._id}"
        class="min-w-0 flex-1"
      />
      <Input
        type="text"
        placeholder="value"
        id="new-css-val-{element._id}"
        class="min-w-0 flex-1"
      />
      <Button
        variant="outline"
        size="icon"
        onclick={() => {
          const propInput = document.getElementById(
            `new-css-prop-${element._id}`
          ) as HTMLInputElement;
          const valInput = document.getElementById(
            `new-css-val-${element._id}`
          ) as HTMLInputElement;
          if (propInput?.value && valInput?.value) {
            onUpdateCustomStyle(propInput.value, valInput.value);
            propInput.value = "";
            valInput.value = "";
          }
        }}
      >
        <Plus class="size-4" />
      </Button>
    </div>
  </div>
</div>
