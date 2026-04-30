<script lang="ts">
  import { AlertRuleSeverity } from "$api-clients";
  import * as Select from "$lib/components/ui/select";
  import { Input } from "$lib/components/ui/input";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";

  interface Props {
    name: string;
    description: string;
    severity: AlertRuleSeverity;
    sortOrder: number;
    isEnabled: boolean;
  }

  let {
    name = $bindable(),
    description = $bindable(),
    severity = $bindable(),
    sortOrder = $bindable(),
    isEnabled = $bindable(),
  }: Props = $props();

  const severityLabels: Record<AlertRuleSeverity, string> = {
    [AlertRuleSeverity.Critical]: "Critical",
    [AlertRuleSeverity.Warning]: "Warning",
    [AlertRuleSeverity.Info]: "Info",
  };
</script>

<div class="space-y-4">
  <div class="space-y-2">
    <Label for="rule-name">Name</Label>
    <Input id="rule-name" bind:value={name} placeholder="Rule name" />
  </div>

  <div class="space-y-2">
    <Label for="rule-description">Description (optional)</Label>
    <Input
      id="rule-description"
      bind:value={description}
      placeholder="Brief description"
    />
  </div>

  <div class="space-y-2">
    <Label for="rule-severity">Severity</Label>
    <Select.Root type="single" bind:value={severity}>
      <Select.Trigger id="rule-severity">
        {severityLabels[severity] ?? severity}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value={AlertRuleSeverity.Critical} label="Critical" />
        <Select.Item value={AlertRuleSeverity.Warning} label="Warning" />
        <Select.Item value={AlertRuleSeverity.Info} label="Info" />
      </Select.Content>
    </Select.Root>
    {#if severity === AlertRuleSeverity.Critical}
      <p class="text-xs text-muted-foreground">
        Critical alerts bypass quiet hours
      </p>
    {/if}
  </div>

  <Separator />

  <div class="grid grid-cols-2 gap-4">
    <div class="space-y-2">
      <Label for="sort-order">Sort Order</Label>
      <Input id="sort-order" type="number" bind:value={sortOrder} />
    </div>
    <div class="flex items-end gap-3 pb-1">
      <div class="space-y-2">
        <Label>Enabled</Label>
        <Switch bind:checked={isEnabled} />
      </div>
    </div>
  </div>
</div>
