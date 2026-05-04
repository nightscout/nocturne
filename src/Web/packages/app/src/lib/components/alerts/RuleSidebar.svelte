<script lang="ts">
  import { onMount, untrack } from "svelte";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Switch } from "$lib/components/ui/switch";
  import {
    ChevronRight,
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
    CalendarDays,
    Moon,
    Ban,
    Timer,
  } from "lucide-svelte";
  import type { AlertRuleResponse } from "$api-clients";
  import type { ConditionNode } from "./types";
  import {
    assignLeafIds,
    composeRuleTruth,
    type LeafTransitionLog,
  } from "./leafEval";
  import { FactSnapshotLog, leafFactBinding } from "./factSnapshot";
  import { rowLeafNode, rowLeafKind } from "./ruleTree";
  import { FACT_GROUP_COLOURS, getFact, type LucideIconName } from "./factCatalog";
  import { summarizeCondition } from "./summarizeCondition";
  import { severity as severitySlot, severityVar } from "./severity";

  interface Props {
    rules: AlertRuleResponse[];
    editingRuleId?: string;
    treeByRule: Map<string, ConditionNode>;
    leafIdsByRule: Map<string, Map<string, number>>;
    leafLog: LeafTransitionLog;
    /** Per-tick numeric fact snapshots used to annotate comparison leaves with the
     * underlying value at the playhead (e.g. "Site age &lt; 3d · 1.2d"). Optional —
     * the sidebar renders without annotations when not provided. */
    factLog?: FactSnapshotLog;
    currentTimeMs: number;
    disabledRuleIds: Set<string>;
    availableRules: { id: string; name: string }[];
  }

  let {
    rules,
    editingRuleId,
    treeByRule,
    leafIdsByRule,
    leafLog,
    factLog,
    currentTimeMs,
    disabledRuleIds = $bindable(),
    availableRules,
  }: Props = $props();

  /** Looks up the comparison-leaf's current value at the playhead and renders it
   * via the leaf's formatter. Returns null when no fact key matches the leaf
   * type, the fact wasn't observed in this replay, or no factLog was provided. */
  function leafCurrentValue(node: ConditionNode): string | null {
    if (!factLog) return null;
    const binding = leafFactBinding(node);
    if (!binding) return null;
    const v = factLog.valueAt(binding.factKey, currentTimeMs);
    if (v === undefined) return null;
    return binding.format(v);
  }

  const STORAGE_KEY = "nocturne.alertSim.disabledRules";

  // Hydrate disabled-rule selection from sessionStorage on mount so the user's
  // ON/OFF preferences persist across replay runs within a tab.
  onMount(() => {
    if (typeof sessionStorage === "undefined") return;
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) {
        disabledRuleIds = new Set(parsed.filter((x) => typeof x === "string"));
      }
    } catch {
      // Ignore malformed storage payloads.
    }
  });

  // Persist on every change. untrack prevents the writer from re-subscribing
  // to its own write (which would flip the set's identity needlessly).
  $effect(() => {
    const ids = [...disabledRuleIds];
    untrack(() => {
      if (typeof sessionStorage === "undefined") return;
      try {
        sessionStorage.setItem(STORAGE_KEY, JSON.stringify(ids));
      } catch {
        // Quota / private mode — silently drop.
      }
    });
  });

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
    "calendar-days": CalendarDays,
    moon: Moon,
  };

  // Pin the rule under edit to the top so it's always visible. Other rules
  // keep their saved sortOrder.
  const orderedRules = $derived.by<AlertRuleResponse[]>(() => {
    if (!editingRuleId) return rules;
    const editing = rules.find((r) => r.id === editingRuleId);
    if (!editing) return rules;
    return [editing, ...rules.filter((r) => r.id !== editingRuleId)];
  });

  const ruleById = $derived(
    new Map(rules.filter((r) => r.id).map((r) => [r.id as string, r])),
  );

  function nameLookup(id: string): string | undefined {
    return availableRules.find((r) => r.id === id)?.name;
  }

  // Collect leaves of a tree in DFS pre-order, matching assignLeafIds. Returns
  // both the wrapping node (for NOT/SUSTAINED context) and the underlying leaf.
  interface LeafEntry {
    leafId: number;
    node: ConditionNode; // the leaf itself (after unwrapping NOT/SUSTAINED)
    wrapper: ConditionNode; // the outer node (== node for plain leaves)
  }

  function collectLeaves(
    rule: AlertRuleResponse,
    tree: ConditionNode,
  ): LeafEntry[] {
    const out: LeafEntry[] = [];
    const ruleId = rule.id;
    if (!ruleId) return out;
    const ids = leafIdsByRule.get(ruleId);
    if (!ids) return out;
    walkLeaves(tree, out, ids);
    return out;
  }

  function walkLeaves(
    node: ConditionNode,
    out: LeafEntry[],
    ids: Map<string, number>,
  ): void {
    // Composite groups: descend into each child, treating it as a row whose
    // top-level node (possibly NOT/SUSTAINED) is the wrapper for display.
    if (node.type === "composite") {
      if (node.composite?.conditions) {
        for (const child of node.composite.conditions) walkLeaves(child, out, ids);
      }
      return;
    }
    // Row node — unwrap NOT/SUSTAINED to find the underlying leaf for the leaf
    // id lookup, but keep `node` as the wrapper so the row can show "not"/sustained
    // chrome and invert pip truth correctly.
    const leafNode = rowLeafNode(node);
    if (!leafNode._uid) return;
    const leafId = ids.get(leafNode._uid);
    if (leafId === undefined) return;
    out.push({ leafId, node: leafNode, wrapper: node });
  }

  // Compose-truth helper bound to the current state. Used by both the rule
  // status pip and per-leaf pips so they always agree.
  function ruleTruth(rule: AlertRuleResponse): boolean {
    const id = rule.id;
    if (!id) return false;
    const tree = treeByRule.get(id);
    if (!tree) return false;
    if (disabledRuleIds.has(id)) return false;
    try {
      return composeRuleTruth(rule, tree, leafLog, currentTimeMs, {
        ruleById,
        treeByRule,
        leafIdsByRule,
        disabledRuleIds,
      });
    } catch {
      return false;
    }
  }

  function leafTruth(
    rule: AlertRuleResponse,
    leafId: number,
    wrapper: ConditionNode,
  ): boolean {
    if (!rule.id) return false;
    const raw = leafLog.valueAt(rule.id, leafId, currentTimeMs) ?? false;
    return wrapper.type === "not" ? !raw : raw;
  }

  function toggleDisabled(id: string, enabled: boolean): void {
    const next = new Set(disabledRuleIds);
    if (enabled) next.delete(id);
    else next.add(id);
    disabledRuleIds = next;
  }
</script>

{#snippet leafIconFor(node: ConditionNode)}
  {@const kind = rowLeafKind(node)}
  {@const fact = kind ? getFact(kind) : undefined}
  {@const colours = fact ? FACT_GROUP_COLOURS[fact.group] : null}
  {@const Icon = fact ? ICONS[fact.icon] : null}
  {#if Icon && colours}
    <span
      class="grid h-5 w-5 shrink-0 place-items-center rounded {colours.bg} {colours.fg}"
      aria-hidden="true"
    >
      <Icon class="h-3 w-3" />
    </span>
  {:else}
    <span
      class="grid h-5 w-5 shrink-0 place-items-center rounded bg-muted"
      aria-hidden="true"
    ></span>
  {/if}
{/snippet}

<div class="flex flex-col gap-2" data-testid="rule-sidebar">
  {#each orderedRules as rule (rule.id ?? rule.name)}
    {@const id = rule.id ?? ""}
    {@const disabled = disabledRuleIds.has(id)}
    {@const truth = ruleTruth(rule)}
    {@const tree = treeByRule.get(id)}
    {@const leaves = tree ? collectLeaves(rule, tree) : []}
    {@const isEditing = editingRuleId === id}

    <Collapsible.Root open={isEditing} class="rounded-md border bg-background">
      <div class="flex items-center gap-2 px-2 py-1.5">
        <Collapsible.Trigger
          class="group flex flex-1 min-w-0 items-center gap-2 text-left text-sm"
        >
          <ChevronRight
            class="h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform group-data-[state=open]:rotate-90"
          />
          <span
            data-testid="rule-status-pip"
            data-truth={truth ? "true" : "false"}
            class="inline-block h-2.5 w-2.5 shrink-0 rounded-full"
            class:opacity-50={disabled}
            style:background-color={truth ? severityVar(rule.severity) : "transparent"}
            style:border={`1.5px solid ${severityVar(rule.severity)}`}
            aria-hidden="true"
          ></span>
          <span class="flex-1 min-w-0 truncate">
            {rule.name ?? "(unnamed)"}
            {#if isEditing}
              <span class="ml-1 text-[10px] uppercase tracking-wide text-muted-foreground">
                (editing)
              </span>
            {/if}
          </span>
          <span
            class="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide {severitySlot(rule.severity, 'chip')}"
          >
            {disabled ? "Off" : "On"}
          </span>
        </Collapsible.Trigger>
        <Switch
          checked={!disabled}
          onCheckedChange={(c) => id && toggleDisabled(id, c)}
          aria-label={disabled ? `Enable ${rule.name}` : `Disable ${rule.name}`}
        />
      </div>
      <Collapsible.Content class="border-t px-2 py-1.5">
        {#if leaves.length === 0}
          <p class="text-xs text-muted-foreground">No leaves to evaluate.</p>
        {:else}
          <ul class="space-y-1">
            {#each leaves as entry (entry.leafId)}
              {@const lt = leafTruth(rule, entry.leafId, entry.wrapper)}
              {@const isNot = entry.wrapper.type === "not"}
              {@const isSustained =
                entry.wrapper.type === "sustained" ||
                (isNot && entry.wrapper.not?.child.type === "sustained")}
              {@const currentValue = leafCurrentValue(entry.node)}
              <li
                data-testid="rule-leaf"
                data-leaf-id={entry.leafId}
                data-truth={lt ? "true" : "false"}
                class="flex items-center gap-2 text-xs"
                class:opacity-30={disabled}
              >
                <span
                  class="inline-block h-2 w-2 shrink-0 rounded-full"
                  class:bg-status-normal={lt}
                  class:bg-muted-foreground={!lt}
                  class:opacity-30={!lt}
                  aria-hidden="true"
                ></span>
                {@render leafIconFor(entry.wrapper)}
                {#if isNot}
                  <Ban class="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" />
                {/if}
                {#if isSustained}
                  <Timer class="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" />
                {/if}
                <span class="flex-1 min-w-0 truncate">
                  {summarizeCondition(entry.wrapper, { resolveAlertName: nameLookup })}
                  {#if currentValue}
                    <span
                      class="ml-1 text-muted-foreground tabular-nums"
                      data-testid="rule-leaf-current-value"
                    >· {currentValue}</span>
                  {/if}
                </span>
              </li>
            {/each}
          </ul>
        {/if}
      </Collapsible.Content>
    </Collapsible.Root>
  {/each}
</div>
