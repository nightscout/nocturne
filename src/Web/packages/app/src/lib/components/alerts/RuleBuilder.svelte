<script lang="ts">
  import { Brackets } from "lucide-svelte";
  import type { ConditionNode } from "./types";
  import OperatorToggle from "./OperatorToggle.svelte";
  import RuleBuilderRow from "./RuleBuilderRow.svelte";
  import RuleBuilderAddPicker from "./RuleBuilderAddPicker.svelte";
  import { addGroup, addLeaf } from "./ruleTree";
  import type { LeafKind } from "./factCatalog";
  import { drag } from "./dragState.svelte";
  import { sendRow, receiveRow } from "./transitions";
  import { flip } from "svelte/animate";

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

  function setOperator(next: "and" | "or"): void {
    if (node.composite) node.composite.operator = next;
  }

  // Shift-hover affordance: when the user holds Shift while hovering a row,
  // the row reveals an inline delete button. Tracked at component scope (the
  // global keydown/keyup state is the same for every row in the group).
  let shiftHeld = $state(false);
</script>

<svelte:window
  onkeydown={(e) => {
    if (e.key === "Shift") shiftHeld = true;
  }}
  onkeyup={(e) => {
    if (e.key === "Shift") shiftHeld = false;
  }}
  onblur={() => (shiftHeld = false)}
/>

<div class="space-y-2">
  {#if !nested && node.composite}
    <div class="flex items-center gap-2 text-sm text-muted-foreground">
      <span>Notify when</span>
      <OperatorToggle
        value={node.composite.operator as "and" | "or"}
        onChange={setOperator}
      />
      <span>these are true:</span>
    </div>
  {:else if nested && node.composite}
    <div class="flex items-center gap-2 text-xs text-muted-foreground">
      <Brackets class="h-3.5 w-3.5" />
      <span>Group — match</span>
      <OperatorToggle
        value={node.composite.operator as "and" | "or"}
        size="compact"
        onChange={setOperator}
      />
    </div>
  {/if}

  <div class="space-y-1.5 {nested ? 'pl-3 border-l border-border/60' : ''}">
    {#if node.composite}
      {#each node.composite.conditions as child, i (child._uid)}
        <div
          in:receiveRow={{ key: child._uid }}
          out:sendRow={{ key: child._uid }}
          animate:flip={{ duration: 220 }}
        >
          <RuleBuilderRow parent={node} index={i} {availableRules} {shiftHeld} />
        </div>
      {/each}
    {/if}

    {#if drag.source && node.composite && !drag.containsSource(node)}
      {@const tailKey = `above:${node._uid ?? "root"}:end`}
      <div
        role="presentation"
        class="h-3 -mt-0.5 rounded transition-colors"
        class:bg-primary={drag.overKey === tailKey}
        ondragover={(e) => {
          e.preventDefault();
          if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
          drag.overKey = tailKey;
        }}
        ondragleave={() => {
          if (drag.overKey === tailKey) drag.overKey = null;
        }}
        ondrop={(e) => {
          e.preventDefault();
          e.stopPropagation();
          if (node.composite) drag.dropInto(node, node.composite.conditions.length);
        }}
      ></div>
    {/if}

    <RuleBuilderAddPicker
      onAddLeaf={(kind: LeafKind) => addLeaf(node, kind)}
      onAddGroup={(op) => addGroup(node, op)}
    />
  </div>
</div>
