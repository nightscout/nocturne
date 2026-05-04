<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import {
    X,
    Ban,
    Brackets,
    Timer,
    MoreHorizontal,
    Trash2,
    Droplet,
    TrendingUp,
    Syringe,
    Apple,
    Clock,
    AlertTriangle,
    Battery,
    BatteryLow,
    Smartphone,
    Fuel,
    RotateCcw,
    WifiOff,
    PauseCircle,
    Wand2,
    ChartLine,
    Activity,
    Bell,
    BellOff,
    CalendarClock,
  } from "lucide-svelte";
  import type { ConditionNode } from "./types";
  import type { TrendBucket } from "./types";
  import { Direction } from "$lib/api";
  import { getDirectionInfo } from "$lib/utils";
  import RuleBuilder from "./RuleBuilder.svelte";
  import RuleBuilderLeafEditor from "./RuleBuilderLeafEditor.svelte";
  import { FACT_GROUP_COLOURS, getFact, type LucideIconName } from "./factCatalog";
  import {
    eyebrow,
    removeChild,
    rowLeafKind,
    rowLeafNode,
    unwrapChild,
    wrapChild,
  } from "./ruleTree";
  import { drag } from "./dragState.svelte";
  import { GripVertical } from "lucide-svelte";

  interface AvailableRule {
    id: string;
    name: string;
  }

  interface Props {
    parent: ConditionNode;
    index: number;
    availableRules?: AvailableRule[];
    /** Tracked at RuleBuilder scope; passed in so every row in a group reacts to the same shift state. */
    shiftHeld: boolean;
  }

  let { parent, index, availableRules = [], shiftHeld }: Props = $props();

  let child = $derived(parent.composite!.conditions[index]);
  let operator = $derived(parent.composite!.operator as "and" | "or");

  const ICONS: Record<LucideIconName, typeof Droplet> = {
    droplet: Droplet,
    "trending-up": TrendingUp,
    syringe: Syringe,
    apple: Apple,
    clock: Clock,
    "alert-triangle": AlertTriangle,
    battery: Battery,
    "battery-low": BatteryLow,
    smartphone: Smartphone,
    fuel: Fuel,
    "rotate-ccw": RotateCcw,
    "wifi-off": WifiOff,
    "pause-circle": PauseCircle,
    "wand-2": Wand2,
    "chart-line": ChartLine,
    activity: Activity,
    bell: Bell,
    "bell-off": BellOff,
    "calendar-clock": CalendarClock,
  };

  // Map the alert builder's trend buckets to the shared `Direction` enum so
  // the row icon matches whatever `getDirectionInfo` renders elsewhere
  // (dashboard, command palette, etc.) — single source of truth for "how does
  // a falling trend look".
  const TREND_DIRECTIONS: Record<TrendBucket, Direction> = {
    falling_fast: Direction.DoubleDown,
    falling: Direction.SingleDown,
    flat: Direction.Flat,
    rising: Direction.SingleUp,
    rising_fast: Direction.DoubleUp,
  };

  function trendIconFor(c: ConditionNode): typeof TrendingUp | null {
    const leaf = rowLeafNode(c);
    if (leaf.type !== "trend" || !leaf.trend) return null;
    const bucket = (leaf.trend.bucket as TrendBucket) ?? "falling";
    return getDirectionInfo(TREND_DIRECTIONS[bucket]).icon ?? TrendingUp;
  }

  let leafKind = $derived(rowLeafKind(child));
  let fact = $derived(leafKind ? getFact(leafKind) : undefined);
  let colours = $derived(fact ? FACT_GROUP_COLOURS[fact.group] : null);
  let TrendIcon = $derived(trendIconFor(child));
  let Icon = $derived(TrendIcon ?? (fact ? ICONS[fact.icon] : null));
  let leafTarget = $derived(rowLeafNode(child));
  let isNot = $derived(child.type === "not");
  let isSustained = $derived(
    child.type === "sustained" ||
      (isNot && child.not?.child.type === "sustained"),
  );
  let sustainedNode = $derived(
    child.type === "sustained"
      ? child
      : isNot && child.not?.child.type === "sustained"
        ? child.not.child
        : null,
  );
  let label = $derived(eyebrow(index, operator));

  // Drag-and-drop wiring. Rows are draggable as a whole; a thin "above-this-
  // row" zone reorders within the same group, while dropping anywhere on a
  // group row inserts the moving node into that group at the end.
  let rowKey = $derived(`${parent._uid ?? "root"}:${index}`);
  let isDragSource = $derived(
    drag.source?.parent === parent && drag.source.index === index,
  );
  let isOverAbove = $derived(drag.overKey === `above:${rowKey}`);
  let isOverGroup = $derived(
    child.type === "composite" && drag.overKey === `into:${child._uid ?? ""}`,
  );

  function startDrag(e: DragEvent): void {
    if (!e.dataTransfer) return;
    e.dataTransfer.effectAllowed = "move";
    // Some browsers refuse to start a drag without payload data.
    e.dataTransfer.setData("text/plain", "alert-condition");
    drag.begin(parent, index);
  }

  function onDragOverAbove(e: DragEvent): void {
    if (!drag.source) return;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
    drag.overKey = `above:${rowKey}`;
  }

  function onDropAbove(e: DragEvent): void {
    e.preventDefault();
    e.stopPropagation();
    drag.dropInto(parent, index);
  }

  function onDragOverGroup(e: DragEvent): void {
    if (!drag.source) return;
    if (child.type !== "composite" || !child.composite) return;
    if (drag.containsSource(child)) return;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
    drag.overKey = `into:${child._uid ?? ""}`;
  }

  function onDropGroup(e: DragEvent): void {
    if (child.type !== "composite" || !child.composite) return;
    if (drag.containsSource(child)) return;
    e.preventDefault();
    e.stopPropagation();
    drag.dropInto(child, child.composite.conditions.length);
  }
</script>

<div class="relative">
  <!-- Drop zone above the row: reorders within the same group. -->
  <div
    role="presentation"
    class="absolute -top-1 left-0 right-0 h-2 z-10"
    class:pointer-events-none={!drag.source || isDragSource}
    ondragover={onDragOverAbove}
    ondragleave={() => {
      if (drag.overKey === `above:${rowKey}`) drag.overKey = null;
    }}
    ondrop={onDropAbove}
  ></div>
  {#if isOverAbove}
    <div
      class="absolute -top-px left-0 right-0 h-0.5 bg-primary rounded-full pointer-events-none z-10"
      aria-hidden="true"
    ></div>
  {/if}
{#if child.type === "composite"}
  <!-- Nested group: indented IFTTT block with eyebrow + actions row above -->
  <div
    class="rounded-md border bg-background p-2 space-y-2 transition-colors"
    class:opacity-50={isDragSource}
    class:ring-2={isOverGroup}
    class:ring-primary={isOverGroup}
    draggable="true"
    ondragstart={startDrag}
    ondragend={() => drag.end()}
    ondragover={onDragOverGroup}
    ondragleave={(e) => {
      // Only clear when leaving the element itself, not when crossing into a
      // descendant — relatedTarget being null or outside this element marks
      // the real exit.
      const next = e.relatedTarget as Node | null;
      if (!next || !(e.currentTarget as HTMLElement).contains(next)) {
        if (drag.overKey === `into:${child._uid ?? ""}`) drag.overKey = null;
      }
    }}
    ondrop={onDropGroup}
  >
    <div class="flex items-center gap-2">
      <button
        type="button"
        class="grid h-6 w-4 shrink-0 cursor-grab place-items-center text-muted-foreground hover:text-foreground"
        aria-label="Drag to reorder"
        tabindex="-1"
      >
        <GripVertical class="h-3.5 w-3.5" />
      </button>
      <span
        class="w-12 shrink-0 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground"
      >
        {label}
      </span>
      <div class="flex-1">
        <RuleBuilder bind:node={parent.composite!.conditions[index]} {availableRules} nested />
      </div>
      <DropdownMenu.Root>
        <DropdownMenu.Trigger>
          {#snippet child({ props })}
            <Button
              {...props}
              variant="ghost"
              size="icon"
              class="h-7 w-7 shrink-0"
              aria-label="Group actions"
            >
              <MoreHorizontal class="h-4 w-4" />
            </Button>
          {/snippet}
        </DropdownMenu.Trigger>
        <DropdownMenu.Content align="end">
          <DropdownMenu.Item onclick={() => wrapChild(parent, index, "not")}>
            <Ban class="h-4 w-4 mr-2" /> Wrap in NOT
          </DropdownMenu.Item>
          <DropdownMenu.Item onclick={() => unwrapChild(parent, index)}>
            Unwrap (when single child)
          </DropdownMenu.Item>
          <DropdownMenu.Separator />
          <DropdownMenu.Item onclick={() => removeChild(parent, index)}>
            <X class="h-4 w-4 mr-2" /> Remove group
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    </div>
  </div>
{:else}
  <!-- Leaf row (possibly wrapped in NOT/SUSTAINED) -->
  <div
    class="group/row flex items-center gap-2 rounded-md border bg-background px-2 py-1.5 transition-opacity"
    class:opacity-50={isDragSource}
    draggable="true"
    ondragstart={startDrag}
    ondragend={() => drag.end()}
  >
    <button
      type="button"
      class="grid h-6 w-4 shrink-0 cursor-grab place-items-center text-muted-foreground hover:text-foreground"
      aria-label="Drag to reorder"
      tabindex="-1"
    >
      <GripVertical class="h-3.5 w-3.5" />
    </button>
    <span
      class="w-12 shrink-0 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground"
    >
      {label}
    </span>
    {#if Icon && colours}
      <span
        class="grid h-6 w-6 shrink-0 place-items-center rounded {colours.bg} {colours.fg}"
        aria-hidden="true"
      >
        <Icon class="h-3.5 w-3.5" />
      </span>
    {/if}
    {#if isNot}
      <span class="rounded bg-muted px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
        NOT
      </span>
    {/if}
    <span class="text-sm font-medium shrink-0">{fact?.label ?? child.type}</span>
    <RuleBuilderLeafEditor node={leafTarget} {availableRules} />
    {#if isSustained && sustainedNode?.sustained}
      <span class="text-xs text-muted-foreground shrink-0">for at least</span>
      <Input
        type="number"
        min="1"
        class="h-7 w-16 px-2 text-right text-xs tabular-nums"
        value={sustainedNode.sustained.minutes ?? 15}
        oninput={(e) => {
          if (sustainedNode?.sustained) {
            const n = Number(e.currentTarget.value);
            sustainedNode.sustained.minutes = Number.isFinite(n)
              ? n
              : sustainedNode.sustained.minutes;
          }
        }}
      />
      <span class="text-xs text-muted-foreground shrink-0">min</span>
    {/if}
    <span class="flex-1"></span>
    {#if shiftHeld}
      <Button
        type="button"
        variant="ghost"
        size="icon"
        class="h-7 w-7 shrink-0 text-destructive hidden group-hover/row:inline-flex"
        aria-label="Remove condition (shift+click shortcut)"
        onclick={() => removeChild(parent, index)}
      >
        <Trash2 class="h-4 w-4" />
      </Button>
    {/if}
    <DropdownMenu.Root>
      <DropdownMenu.Trigger>
        {#snippet child({ props })}
          <Button
            {...props}
            variant="ghost"
            size="icon"
            class="h-7 w-7 shrink-0"
            aria-label="Row actions"
          >
            <MoreHorizontal class="h-4 w-4" />
          </Button>
        {/snippet}
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="end">
        <DropdownMenu.Item onclick={() => wrapChild(parent, index, "and")}>
          <Brackets class="h-4 w-4 mr-2" /> Wrap in AND group
        </DropdownMenu.Item>
        <DropdownMenu.Item onclick={() => wrapChild(parent, index, "or")}>
          <Brackets class="h-4 w-4 mr-2" /> Wrap in OR group
        </DropdownMenu.Item>
        <DropdownMenu.Item onclick={() => wrapChild(parent, index, "not")}>
          <Ban class="h-4 w-4 mr-2" /> Wrap in NOT
        </DropdownMenu.Item>
        {#if !isSustained}
          <DropdownMenu.Item onclick={() => wrapChild(parent, index, "sustained")}>
            <Timer class="h-4 w-4 mr-2" /> Make sustained
          </DropdownMenu.Item>
        {/if}
        {#if isNot || isSustained}
          <DropdownMenu.Separator />
          <DropdownMenu.Item onclick={() => unwrapChild(parent, index)}>
            Remove wrapper
          </DropdownMenu.Item>
        {/if}
        <DropdownMenu.Separator />
        <DropdownMenu.Item onclick={() => removeChild(parent, index)}>
          <X class="h-4 w-4 mr-2" /> Remove
        </DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
  </div>
{/if}
</div>
