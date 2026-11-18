<script lang="ts">
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Wand2 from "@lucide/svelte/icons/wand-sparkles";
  import RuleBuilder from "./RuleBuilder.svelte";
  import {
    defaultPayload,
    ensureCompositeRoot,
    type ConditionNode,
  } from "./types";
  import { toast } from "svelte-sonner";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { oppositeCondition } from "./oppositeCondition.js";

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

  let suggesting = $state(false);

  async function applySuggestion(): Promise<void> {
    const replaces = enabled && (condition?.composite?.conditions.length ?? 0) > 0;
    if (replaces && !confirm("Replace the conditions with the suggestion?")) return;
    suggesting = true;
    try {
      const suggested = await oppositeCondition(firingCondition);
      condition = suggested ?? ensureCompositeRoot(defaultPayload("threshold"));
      enabled = true;
    } catch (e) {
      toast.error(describeSubmitError(e, "Could not get a suggestion. Try again."));
    } finally {
      suggesting = false;
    }
  }
</script>

<div class="space-y-3">
  <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
    <div class="min-w-48 flex-1">
      <Label for="auto-resolve-enabled" variant="option">
        Close the alert automatically when a condition is true, also while its own conditions are
        still true.
      </Label>
    </div>
    <div class="flex shrink-0 items-center gap-2">
      <Button
        type="button"
        variant="outline"
        size="sm"
        onclick={applySuggestion}
        disabled={!firingCondition || suggesting}
        title="Start from the opposite of the alert's conditions"
      >
        <Wand2 class="h-3.5 w-3.5 mr-2" /> Suggest
      </Button>
      <Switch id="auto-resolve-enabled" checked={enabled} onCheckedChange={onToggle} />
    </div>
  </div>

  {#if enabled && condition !== null}
    <RuleBuilder bind:node={condition} {availableRules} purpose="resolve" />
  {:else if enabled}
    <p class="text-sm text-muted-foreground">
      Add a condition that signals when the alert resolves.
    </p>
  {/if}
</div>
