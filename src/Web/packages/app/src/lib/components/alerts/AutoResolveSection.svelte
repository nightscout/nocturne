<script lang="ts">
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import RuleBuilder from "./RuleBuilder.svelte";
  import { defaultPayload, type ConditionNode } from "./types";

  interface AvailableRule {
    id: string;
    name: string;
  }

  interface Props {
    enabled: boolean;
    condition: ConditionNode | null;
    availableRules?: AvailableRule[];
  }

  let {
    enabled = $bindable(),
    condition = $bindable(),
    availableRules = [],
  }: Props = $props();

  function onToggle(next: boolean) {
    enabled = next;
    if (next && condition === null) {
      condition = defaultPayload("threshold");
    }
  }
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div>
      <Label>Auto-resolve</Label>
      <p class="text-xs text-muted-foreground">
        Close the alert automatically when this condition is true.
      </p>
    </div>
    <Switch checked={enabled} onCheckedChange={onToggle} />
  </div>

  {#if enabled && condition !== null}
    <RuleBuilder bind:node={condition} {availableRules} />
  {:else if enabled}
    <p class="text-sm text-muted-foreground">
      Add a condition that signals the alert should resolve.
    </p>
  {/if}
</div>
