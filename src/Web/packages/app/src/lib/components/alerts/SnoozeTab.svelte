<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import { Plus, X } from "lucide-svelte";
  import RuleBuilder from "./RuleBuilder.svelte";
  import { defaultPayload, type ConditionNode, type SnoozeConfig } from "./types";

  interface AvailableRule {
    id: string;
    name: string;
  }

  interface Props {
    snooze: SnoozeConfig;
    availableRules?: AvailableRule[];
  }

  let { snooze = $bindable(), availableRules = [] }: Props = $props();

  let newSnoozeOption = $state("");

  function addSnoozeOption() {
    const val = parseInt(newSnoozeOption, 10);
    if (!isNaN(val) && val > 0 && !snooze.options.includes(val)) {
      snooze.options = [...snooze.options, val].sort((a, b) => a - b);
      newSnoozeOption = "";
    }
  }

  function removeSnoozeOption(val: number) {
    snooze.options = snooze.options.filter((o) => o !== val);
  }

  function addCondition() {
    snooze.conditions = [...snooze.conditions, defaultPayload("threshold")];
  }

  function removeCondition(index: number) {
    snooze.conditions = snooze.conditions.filter((_, i) => i !== index);
  }
</script>

<div class="space-y-4">
  <div class="space-y-2">
    <Label for="snooze-default">Default Snooze Duration (minutes)</Label>
    <Input
      id="snooze-default"
      type="number"
      bind:value={snooze.defaultMinutes}
    />
  </div>

  <div class="space-y-2">
    <Label>Snooze Options</Label>
    <div class="flex flex-wrap gap-2">
      {#each snooze.options as opt (opt)}
        <Badge variant="secondary" class="gap-1 pr-1">
          {opt}m
          <button
            class="ml-1 rounded-full hover:bg-muted-foreground/20 p-0.5"
            onclick={() => removeSnoozeOption(opt)}
            aria-label={`Remove ${opt} minute option`}
          >
            <X class="h-3 w-3" />
          </button>
        </Badge>
      {/each}
    </div>
    <div class="flex gap-2">
      <Input
        placeholder="Minutes"
        type="number"
        bind:value={newSnoozeOption}
        class="w-24"
        onkeydown={(e: KeyboardEvent) => {
          if (e.key === "Enter") {
            e.preventDefault();
            addSnoozeOption();
          }
        }}
      />
      <Button variant="outline" size="sm" onclick={addSnoozeOption}>
        Add
      </Button>
    </div>
  </div>

  <div class="space-y-2">
    <Label for="snooze-max-count">Max Snooze Count</Label>
    <Input
      id="snooze-max-count"
      type="number"
      bind:value={snooze.maxCount}
    />
  </div>

  <Separator />

  <div class="flex items-center justify-between">
    <Label>Smart Snooze</Label>
    <Switch bind:checked={snooze.smartSnooze} />
  </div>

  {#if snooze.smartSnooze}
    <div class="space-y-2">
      <Label for="smart-snooze-extend">Smart Snooze Extend (minutes)</Label>
      <Input
        id="smart-snooze-extend"
        type="number"
        bind:value={snooze.smartSnoozeExtendMinutes}
      />
      <p class="text-xs text-muted-foreground">
        How long to extend the snooze when conditions hold. With no
        conditions configured, the backend falls back to a trend-favorable
        heuristic.
      </p>
    </div>

    <div class="space-y-2">
      <div class="flex items-center justify-between">
        <Label>Conditions to extend snooze</Label>
        <Button variant="outline" size="sm" onclick={addCondition}>
          <Plus class="h-4 w-4 mr-2" />
          Add condition
        </Button>
      </div>
      <p class="text-xs text-muted-foreground">
        All conditions must hold for the snooze to extend. Leave empty to use
        the default trend-favorable heuristic.
      </p>
      {#if snooze.conditions.length === 0}
        <p class="text-sm text-muted-foreground italic">
          No conditions configured.
        </p>
      {:else}
        <div class="space-y-2">
          {#each snooze.conditions as _condition, i (i)}
            <RuleBuilder
              bind:node={snooze.conditions[i]}
              {availableRules}
              onRemove={() => removeCondition(i)}
            />
          {/each}
        </div>
      {/if}
    </div>
  {/if}
</div>
