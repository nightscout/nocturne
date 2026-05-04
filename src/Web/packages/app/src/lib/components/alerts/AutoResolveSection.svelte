<script lang="ts">
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import { Wand2 } from "lucide-svelte";
  import RuleBuilder from "./RuleBuilder.svelte";
  import {
    defaultPayload,
    ensureCompositeRoot,
    type ConditionNode,
  } from "./types";
  import { suggestAutoResolve } from "./suggestAutoResolve";

  interface AvailableRule {
    id: string;
    name: string;
  }

  interface Props {
    enabled: boolean;
    /**
     * The auto-resolve tree. The inline rule builder edits at the group level,
     * so this is always a composite when present.
     */
    condition: ConditionNode | null;
    /** The firing tree, used by Suggest to derive a starting inverse. */
    firingCondition: ConditionNode | null;
    availableRules?: AvailableRule[];
  }

  let {
    enabled = $bindable(),
    condition = $bindable(),
    firingCondition,
    availableRules = [],
  }: Props = $props();

  function onToggle(next: boolean): void {
    enabled = next;
    if (next && condition === null) {
      condition = ensureCompositeRoot(defaultPayload("threshold"));
    }
  }

  function applySuggestion(): void {
    const suggested = suggestAutoResolve(firingCondition);
    condition = suggested ?? ensureCompositeRoot(defaultPayload("threshold"));
    enabled = true;
  }
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between gap-2">
    <div>
      <Label>Auto-resolve</Label>
      <p class="text-xs text-muted-foreground">
        Close the alert automatically when this condition is true.
      </p>
    </div>
    <div class="flex items-center gap-2">
      <Button
        type="button"
        variant="outline"
        size="sm"
        onclick={applySuggestion}
        disabled={!firingCondition}
        title="Derive a starting auto-resolve from the firing tree"
      >
        <Wand2 class="h-3.5 w-3.5 mr-2" /> Suggest
      </Button>
      <Switch checked={enabled} onCheckedChange={onToggle} />
    </div>
  </div>

  {#if enabled && condition !== null}
    <RuleBuilder bind:node={condition} {availableRules} />
  {:else if enabled}
    <p class="text-sm text-muted-foreground">
      Add a condition that signals the alert should resolve.
    </p>
  {/if}
</div>
