<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import * as Popover from "$lib/components/ui/popover";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import {
    Plus,
    X,
    Ban,
    Brackets,
    Timer,
    MoreHorizontal,
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
  import Self from "./RuleBuilder.svelte";
  import RuleBuilderLeafEditor from "./RuleBuilderLeafEditor.svelte";
  import {
    defaultPayload,
    type ConditionKind,
    type ConditionNode,
  } from "./types";
  import {
    LEAF_FACTS,
    FACT_GROUP_ORDER,
    FACT_GROUP_LABELS,
    FACT_GROUP_COLOURS,
    getFact,
    type LeafKind,
    type LucideIconName,
  } from "./factCatalog";

  interface AvailableRule {
    id: string;
    name: string;
  }

  interface Props {
    /** Composite-rooted condition tree. The root MUST be a composite — call
     *  `ensureCompositeRoot` from types.ts before mounting if unsure. */
    node: ConditionNode;
    availableRules?: AvailableRule[];
    /** When true, suppress the "Notify when …" preamble (used by recursive
     *  nested groups so only the outermost shows the lead-in). */
    nested?: boolean;
  }

  let { node = $bindable(), availableRules = [], nested = false }: Props = $props();

  // The rule builder always edits at the group level; if a caller hands us a
  // non-composite node, render an empty stub so the runtime doesn't crash —
  // surfacing the bug to the developer console rather than rendering garbage.
  if (node.type !== "composite" || !node.composite) {
    // eslint-disable-next-line no-console
    console.error("[RuleBuilder] expected a composite root, got:", node.type);
  }

  // ---- Lookup table for the icon glyph per fact -------------------------
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

  // ---- Mutations -------------------------------------------------------
  // Mutating the existing array/object via Svelte 5's deep-proxy state
  // propagates back through the parent's bind:node — no reassignment needed
  // for child-level edits. For child *replacement* we splice in place.

  function addLeaf(kind: LeafKind): void {
    if (!node.composite) return;
    node.composite.conditions.push(defaultPayload(kind));
  }

  function addGroup(operator: "and" | "or"): void {
    if (!node.composite) return;
    const seed = defaultPayload("composite");
    if (seed.composite) seed.composite.operator = operator;
    node.composite.conditions.push(seed);
  }

  function removeChild(index: number): void {
    if (!node.composite) return;
    node.composite.conditions.splice(index, 1);
  }

  /**
   * Wrap the child at <paramref name="index"/> in <paramref name="wrapper"/>
   * (and/or → composite, not, sustained), preserving the original node as the
   * wrapper's first/only child. The original `_uid` stays on the inner node so
   * the keyed each block doesn't collapse.
   */
  function wrapChild(index: number, wrapper: "and" | "or" | "not" | "sustained"): void {
    if (!node.composite) return;
    const inner = node.composite.conditions[index];
    let next: ConditionNode;
    if (wrapper === "not") {
      next = { ...defaultPayload("not"), not: { child: inner } };
    } else if (wrapper === "sustained") {
      next = {
        ...defaultPayload("sustained"),
        sustained: { minutes: 15, child: inner },
      };
    } else {
      next = {
        ...defaultPayload("composite"),
        composite: { operator: wrapper, conditions: [inner] },
      };
    }
    node.composite.conditions[index] = next;
  }

  /**
   * Inverse of {@link wrapChild}. If the child at <paramref name="index"/> is
   * a NOT or single-child composite or a sustained, replace it with its inner
   * node. Multi-child composites stay put — flattening them would lose the
   * group's siblings.
   */
  function unwrapChild(index: number): void {
    if (!node.composite) return;
    const c = node.composite.conditions[index];
    if (c.type === "not" && c.not?.child) {
      node.composite.conditions[index] = c.not.child;
    } else if (c.type === "sustained" && c.sustained?.child) {
      node.composite.conditions[index] = c.sustained.child;
    } else if (
      c.type === "composite" &&
      c.composite &&
      c.composite.conditions.length === 1
    ) {
      node.composite.conditions[index] = c.composite.conditions[0];
    }
  }

  function eyebrow(index: number, op: "and" | "or"): string {
    if (index === 0) return "IF";
    return op === "and" ? "AND" : "OR";
  }

  /**
   * Pull the leaf descriptor for a node — for `not(leaf)` and
   * `sustained(leaf)` we display the *inner* leaf's icon and label, so the row
   * still reads like "(NOT) BG &lt; 70 mg/dL (for 15m)" rather than something
   * generic for the wrapper.
   */
  function rowLeafKind(c: ConditionNode): LeafKind | null {
    let cur: ConditionNode = c;
    while (cur.type === "not" && cur.not) cur = cur.not.child;
    while (cur.type === "sustained" && cur.sustained) cur = cur.sustained.child;
    if (cur.type === "composite") return null;
    return cur.type as LeafKind;
  }

  /** Walk past NOT/SUSTAINED wrappers to reach the underlying leaf node. */
  function rowLeafNode(c: ConditionNode): ConditionNode {
    let cur: ConditionNode = c;
    while (cur.type === "not" && cur.not) cur = cur.not.child;
    while (cur.type === "sustained" && cur.sustained) cur = cur.sustained.child;
    return cur;
  }
</script>

<div class="space-y-2">
  {#if !nested && node.composite}
    <div class="flex items-center gap-2 text-sm text-muted-foreground">
      <span>Notify when</span>
      <div class="inline-flex rounded-md border bg-background p-0.5 text-xs font-medium">
        <button
          type="button"
          class="px-2 py-1 rounded {node.composite.operator === 'and'
            ? 'bg-muted text-foreground'
            : 'text-muted-foreground hover:text-foreground'}"
          onclick={() => {
            if (node.composite) node.composite.operator = 'and';
          }}
        >
          all of
        </button>
        <button
          type="button"
          class="px-2 py-1 rounded {node.composite.operator === 'or'
            ? 'bg-muted text-foreground'
            : 'text-muted-foreground hover:text-foreground'}"
          onclick={() => {
            if (node.composite) node.composite.operator = 'or';
          }}
        >
          any of
        </button>
      </div>
      <span>these are true:</span>
    </div>
  {:else if nested && node.composite}
    <div class="flex items-center gap-2 text-xs text-muted-foreground">
      <Brackets class="h-3.5 w-3.5" />
      <span>Group — match</span>
      <div class="inline-flex rounded-md border bg-background p-0.5 font-medium">
        <button
          type="button"
          class="px-1.5 py-0.5 rounded {node.composite.operator === 'and'
            ? 'bg-muted text-foreground'
            : 'text-muted-foreground hover:text-foreground'}"
          onclick={() => {
            if (node.composite) node.composite.operator = 'and';
          }}
        >
          all
        </button>
        <button
          type="button"
          class="px-1.5 py-0.5 rounded {node.composite.operator === 'or'
            ? 'bg-muted text-foreground'
            : 'text-muted-foreground hover:text-foreground'}"
          onclick={() => {
            if (node.composite) node.composite.operator = 'or';
          }}
        >
          any
        </button>
      </div>
    </div>
  {/if}

  <div class="space-y-1.5 {nested ? 'pl-3 border-l border-border/60' : ''}">
    {#if node.composite}
      {#each node.composite.conditions as child, i (child._uid)}
        {@const leafKind = rowLeafKind(child)}
        {@const fact = leafKind ? getFact(leafKind) : undefined}
        {@const colours = fact ? FACT_GROUP_COLOURS[fact.group] : null}
        {@const Icon = fact ? ICONS[fact.icon] : null}

        {#if child.type === "composite"}
          <!-- Nested group: indented IFTTT block with eyebrow + actions row above -->
          <div class="rounded-md border bg-background p-2 space-y-2">
            <div class="flex items-center gap-2">
              <span
                class="w-12 shrink-0 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground"
              >
                {eyebrow(i, node.composite.operator as "and" | "or")}
              </span>
              <div class="flex-1">
                <Self bind:node={node.composite.conditions[i]} {availableRules} nested />
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
                  <DropdownMenu.Item onclick={() => wrapChild(i, "not")}>
                    <Ban class="h-4 w-4 mr-2" /> Wrap in NOT
                  </DropdownMenu.Item>
                  <DropdownMenu.Item onclick={() => unwrapChild(i)}>
                    Unwrap (when single child)
                  </DropdownMenu.Item>
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item onclick={() => removeChild(i)}>
                    <X class="h-4 w-4 mr-2" /> Remove group
                  </DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu.Root>
            </div>
          </div>
        {:else}
          <!-- Leaf row (possibly wrapped in NOT/SUSTAINED) -->
          {@const leafTarget = rowLeafNode(child)}
          {@const isNot = child.type === "not"}
          {@const isSustained = child.type === "sustained" || (isNot && child.not?.child.type === "sustained")}
          {@const sustainedNode = child.type === "sustained" ? child : (isNot && child.not?.child.type === "sustained" ? child.not.child : null)}

          <div class="flex items-center gap-2 rounded-md border bg-background px-2 py-1.5">
            <span
              class="w-12 shrink-0 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground"
            >
              {eyebrow(i, node.composite.operator as "and" | "or")}
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
            <RuleBuilderLeafEditor bind:node={leafTarget} {availableRules} />
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
                    sustainedNode.sustained.minutes = Number.isFinite(n) ? n : sustainedNode.sustained.minutes;
                  }
                }}
              />
              <span class="text-xs text-muted-foreground shrink-0">min</span>
            {/if}
            <span class="flex-1"></span>
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
                <DropdownMenu.Item onclick={() => wrapChild(i, "and")}>
                  <Brackets class="h-4 w-4 mr-2" /> Wrap in AND group
                </DropdownMenu.Item>
                <DropdownMenu.Item onclick={() => wrapChild(i, "or")}>
                  <Brackets class="h-4 w-4 mr-2" /> Wrap in OR group
                </DropdownMenu.Item>
                <DropdownMenu.Item onclick={() => wrapChild(i, "not")}>
                  <Ban class="h-4 w-4 mr-2" /> Wrap in NOT
                </DropdownMenu.Item>
                {#if !isSustained}
                  <DropdownMenu.Item onclick={() => wrapChild(i, "sustained")}>
                    <Timer class="h-4 w-4 mr-2" /> Make sustained
                  </DropdownMenu.Item>
                {/if}
                {#if isNot || isSustained}
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item onclick={() => unwrapChild(i)}>
                    Remove wrapper
                  </DropdownMenu.Item>
                {/if}
                <DropdownMenu.Separator />
                <DropdownMenu.Item onclick={() => removeChild(i)}>
                  <X class="h-4 w-4 mr-2" /> Remove
                </DropdownMenu.Item>
              </DropdownMenu.Content>
            </DropdownMenu.Root>
          </div>
        {/if}
      {/each}
    {/if}

    <!-- "Add condition" picker -->
    <Popover.Root>
      <Popover.Trigger>
        {#snippet child({ props })}
          <Button
            {...props}
            variant="outline"
            size="sm"
            class="border-dashed text-muted-foreground"
          >
            <Plus class="h-4 w-4 mr-2" /> Add condition
          </Button>
        {/snippet}
      </Popover.Trigger>
      <Popover.Content class="w-80 p-1" align="start">
        <div class="max-h-96 overflow-y-auto">
          {#each FACT_GROUP_ORDER as group (group)}
            {@const facts = LEAF_FACTS.filter((f) => f.group === group)}
            {#if facts.length > 0}
              <div class="px-2 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
                {FACT_GROUP_LABELS[group]}
              </div>
              {#each facts as f (f.kind)}
                {@const c = FACT_GROUP_COLOURS[f.group]}
                {@const Glyph = ICONS[f.icon]}
                <Popover.Close>
                  {#snippet child({ props })}
                    <button
                      {...props}
                      type="button"
                      class="flex w-full items-start gap-2 rounded px-2 py-1.5 text-left hover:bg-muted"
                      onclick={() => addLeaf(f.kind)}
                    >
                      <span
                        class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded {c.bg} {c.fg}"
                        aria-hidden="true"
                      >
                        <Glyph class="h-3.5 w-3.5" />
                      </span>
                      <span class="flex flex-col">
                        <span class="text-sm font-medium">{f.label}</span>
                        <span class="text-xs text-muted-foreground leading-tight">{f.description}</span>
                      </span>
                    </button>
                  {/snippet}
                </Popover.Close>
              {/each}
            {/if}
          {/each}

          <div class="my-1 border-t"></div>
          <div class="px-2 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
            Group
          </div>
          <Popover.Close>
            {#snippet child({ props })}
              <button
                {...props}
                type="button"
                class="flex w-full items-start gap-2 rounded px-2 py-1.5 text-left hover:bg-muted"
                onclick={() => addGroup("and")}
              >
                <span class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
                  <Brackets class="h-3.5 w-3.5" />
                </span>
                <span class="flex flex-col">
                  <span class="text-sm font-medium">+ Group (AND)</span>
                  <span class="text-xs text-muted-foreground leading-tight">All sub-conditions must hold</span>
                </span>
              </button>
            {/snippet}
          </Popover.Close>
          <Popover.Close>
            {#snippet child({ props })}
              <button
                {...props}
                type="button"
                class="flex w-full items-start gap-2 rounded px-2 py-1.5 text-left hover:bg-muted"
                onclick={() => addGroup("or")}
              >
                <span class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
                  <Brackets class="h-3.5 w-3.5" />
                </span>
                <span class="flex flex-col">
                  <span class="text-sm font-medium">+ Group (OR)</span>
                  <span class="text-xs text-muted-foreground leading-tight">Any sub-condition is enough</span>
                </span>
              </button>
            {/snippet}
          </Popover.Close>
        </div>
      </Popover.Content>
    </Popover.Root>
  </div>
</div>
