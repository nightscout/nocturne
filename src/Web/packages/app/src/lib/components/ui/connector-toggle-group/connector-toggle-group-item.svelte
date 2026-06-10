<script lang="ts">
  import { ToggleGroup as ToggleGroupPrimitive } from "bits-ui";
  import { cn } from "$lib/utils";
  import { Check, Settings2 } from "@lucide/svelte";
  import * as Item from "../item/index.js";
  import { Badge } from "../badge/index.js";
  import { Button } from "../button/index.js";

  interface ConnectorField {
    name: string;
    envVar: string;
    type: string;
    required?: boolean;
    default?: string;
    description?: string;
    options?: string[];
  }

  let {
    ref = $bindable(null),
    value,
    displayName,
    description,
    category,
    fields = [],
    class: className,
    onConfigure,
    ...restProps
  }: {
    ref?: HTMLButtonElement | null;
    value: string;
    displayName: string;
    description: string;
    category: string;
    fields?: ConnectorField[];
    class?: string;
    onConfigure?: () => void;
  } = $props();

  function getCategoryVariant(
    category: string
  ): "default" | "secondary" | "outline" {
    switch (category.toLowerCase()) {
      case "cgm":
        return "default";
      case "pump":
        return "secondary";
      default:
        return "outline";
    }
  }
</script>

<ToggleGroupPrimitive.Item
  bind:ref
  data-slot="connector-toggle-group-item"
  {value}
  class={cn(
    "group relative flex flex-col h-full transition-all cursor-pointer rounded-xl border bg-card text-card-foreground shadow text-left",
    "hover:border-primary/50",
    "data-[state=on]:border-primary data-[state=on]:ring-1 data-[state=on]:ring-primary/20 data-[state=on]:bg-primary/5",
    className
  )}
  {...restProps}
>
  <div class="flex flex-col space-y-1.5 p-6 pb-3">
    <div class="flex justify-between items-start gap-4">
      <div class="flex gap-4">
        <Item.Media variant="icon" class="bg-muted/50 text-lg shrink-0">
          {displayName.charAt(0)}
        </Item.Media>
        <Item.Content>
          <Item.Title class="text-lg flex items-center gap-2">
            {displayName}
          </Item.Title>
          <div class="mt-1">
            <Badge variant={getCategoryVariant(category)}>
              {category}
            </Badge>
          </div>
        </Item.Content>
      </div>

      <div
        class={cn(
          "w-8 h-8 shrink-0 rounded-full flex items-center justify-center",
          "border border-input bg-background",
          "group-data-[state=on]:bg-primary group-data-[state=on]:text-primary-foreground group-data-[state=on]:border-transparent"
        )}
      >
        <Check
          size={14}
          strokeWidth={3}
          class="opacity-0 group-data-[state=on]:opacity-100 transition-opacity"
        />
      </div>
    </div>
  </div>

  <div class="p-6 pt-0 flex-1 pb-3">
    <Item.Description class="text-sm">
      {description}
    </Item.Description>
  </div>

  {#if fields.length > 0}
    <div class="hidden group-data-[state=on]:flex items-center p-6 pt-0">
      <Button
        variant="secondary"
        size="sm"
        class="w-full gap-2"
        onclick={(e: MouseEvent) => {
          e.stopPropagation();
          onConfigure?.();
        }}
      >
        <Settings2 size={14} />
        Configure
      </Button>
    </div>
  {/if}
</ToggleGroupPrimitive.Item>
