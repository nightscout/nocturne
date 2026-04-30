<script lang="ts">
  import type { AlertRuleResponse } from "$api-clients";
  import { AlertConditionType } from "$api-clients";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Switch } from "$lib/components/ui/switch";
  import { Separator } from "$lib/components/ui/separator";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { bgValue, bgLabel } from "$lib/utils/formatting";
  import {
    Bell,
    Trash2,
    ChevronDown,
    ChevronUp,
    Clock,
    Loader2,
    Shield,
    Zap,
    TrendingDown,
    ArrowUpRight,
    Pencil,
  } from "lucide-svelte";

  interface Props {
    rule: AlertRuleResponse;
    isExpanded: boolean;
    isToggling: boolean;
    isDeleting: boolean;
    onToggleExpand: () => void;
    onToggleEnabled: () => void;
    onEdit: () => void;
    onDelete: () => void;
  }

  let { rule, isExpanded, isToggling, isDeleting, onToggleExpand, onToggleEnabled, onEdit, onDelete }: Props = $props();

  function getConditionIcon(conditionType: AlertConditionType | undefined) {
    switch (conditionType) {
      case AlertConditionType.Threshold:
        return TrendingDown;
      case AlertConditionType.RateOfChange:
        return Zap;
      case AlertConditionType.Composite:
        return Shield;
      default:
        return Bell;
    }
  }

  function getConditionBadgeVariant(
    conditionType: AlertConditionType | undefined,
  ): "default" | "secondary" | "destructive" | "outline" {
    switch (conditionType) {
      case AlertConditionType.Threshold:
        return "destructive";
      default:
        return "outline";
    }
  }

  function getConditionLabel(conditionType: AlertConditionType | undefined): string {
    switch (conditionType) {
      case AlertConditionType.Threshold:
        return "Threshold";
      case AlertConditionType.RateOfChange:
        return "Rate of Change";
      case AlertConditionType.Composite:
        return "Composite";
      default:
        return conditionType ?? "Unknown";
    }
  }

  function getConditionSummary(rule: AlertRuleResponse): string {
    const params = rule.conditionParams;
    if (!params) return "No condition configured";

    const label = bgLabel();

    function formatThreshold(raw: unknown): string {
      const val = Number(raw);
      if (Number.isNaN(val)) return "?";
      return String(bgValue(val));
    }

    switch (rule.conditionType) {
      case AlertConditionType.Threshold: {
        const dir = (params as Record<string, unknown>)["direction"];
        const display = formatThreshold((params as Record<string, unknown>)["threshold"]);
        if (dir === "below") return `Below ${display} ${label}`;
        if (dir === "above") return `Above ${display} ${label}`;
        return `Threshold: ${display} ${label}`;
      }
      case AlertConditionType.RateOfChange: {
        const dir = (params as Record<string, unknown>)["direction"] === "falling" ? "Falling" : "Rising";
        const display = formatThreshold((params as Record<string, unknown>)["rateThreshold"]);
        return `${dir} faster than ${display} ${label}/min`;
      }
      case AlertConditionType.Composite:
        return "Multiple conditions combined";
      default:
        return "Custom condition";
    }
  }
</script>

<div class="rounded-lg border transition-all hover:shadow-sm">
  <!-- Rule Summary Row -->
  {#key rule.id}
    {@const ConditionIcon = getConditionIcon(rule.conditionType)}
    <button
      class="flex items-center gap-4 p-4 w-full text-left"
      onclick={onToggleExpand}
    >
      <ConditionIcon
        class="h-5 w-5 shrink-0 {rule.isEnabled
          ? 'text-primary'
          : 'text-muted-foreground'}"
      />
      <div class="flex-1 min-w-0">
        <div class="flex items-center gap-2 mb-1">
          <span class="font-medium truncate">
            {rule.name ?? "Unnamed Rule"}
          </span>
          <Badge variant={getConditionBadgeVariant(rule.conditionType)}>
            {getConditionLabel(rule.conditionType)}
          </Badge>
          {#if !rule.isEnabled}
            <Badge variant="secondary">Disabled</Badge>
          {/if}
        </div>
        <div
          class="flex items-center gap-4 text-sm text-muted-foreground"
        >
          <span>{getConditionSummary(rule)}</span>
          {#if rule.schedules && rule.schedules.length > 0}
            <span class="flex items-center gap-1">
              <Clock class="h-3 w-3" />
              {rule.schedules.length} schedule{rule.schedules.length !== 1 ? "s" : ""}
            </span>
          {/if}
          {#if rule.schedules}
            {@const stepCount = rule.schedules.reduce(
              (acc, s) => acc + (s.escalationSteps?.length ?? 0),
              0,
            )}
            {#if stepCount > 0}
              <span class="flex items-center gap-1">
                <ArrowUpRight class="h-3 w-3" />
                {stepCount} escalation step{stepCount !== 1 ? "s" : ""}
              </span>
            {/if}
          {/if}
        </div>
      </div>
      <div class="flex items-center gap-2 shrink-0">
        <Switch
          checked={rule.isEnabled ?? false}
          onCheckedChange={onToggleEnabled}
          disabled={isToggling}
          onclick={(e: MouseEvent) => e.stopPropagation()}
        />
        {#if isExpanded}
          <ChevronUp class="h-4 w-4 text-muted-foreground" />
        {:else}
          <ChevronDown class="h-4 w-4 text-muted-foreground" />
        {/if}
      </div>
    </button>
  {/key}

  <!-- Expanded Detail -->
  {#if isExpanded}
    <div class="border-t px-4 py-4 space-y-4 bg-muted/30">
      {#if rule.description}
        <p class="text-sm text-muted-foreground">
          {rule.description}
        </p>
      {/if}

      <div class="grid gap-4 sm:grid-cols-2 text-sm">
        <div>
          <p class="text-muted-foreground mb-1">Severity</p>
          <p class="font-medium">{rule.severity ?? "warning"}</p>
        </div>
        <div>
          <p class="text-muted-foreground mb-1">Sort Order</p>
          <p class="font-medium">{rule.sortOrder ?? 0}</p>
        </div>
      </div>

      <!-- Schedules -->
      {#if rule.schedules && rule.schedules.length > 0}
        <Separator />
        <div>
          <h4 class="text-sm font-medium mb-3">Schedules</h4>
          {#each rule.schedules as schedule (schedule.id)}
            <div class="mb-3 p-3 rounded-md border bg-background">
              <div class="flex items-center gap-2 mb-2">
                <Clock class="h-4 w-4 text-muted-foreground" />
                <span class="text-sm font-medium">
                  {schedule.name ?? "Default Schedule"}
                </span>
                {#if schedule.isDefault}
                  <Badge variant="secondary">Default</Badge>
                {/if}
              </div>
              {#if schedule.startTime || schedule.endTime}
                <p class="text-xs text-muted-foreground mb-2">
                  {schedule.startTime ?? "00:00"} - {schedule.endTime ?? "23:59"}
                  ({schedule.timezone ?? "UTC"})
                </p>
              {/if}
              {#if schedule.daysOfWeek && schedule.daysOfWeek.length > 0 && schedule.daysOfWeek.length < 7}
                <p class="text-xs text-muted-foreground mb-2">
                  Days: {schedule.daysOfWeek.map((d) => ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][d] ?? d).join(", ")}
                </p>
              {/if}

              <!-- Escalation Steps -->
              {#if schedule.escalationSteps && schedule.escalationSteps.length > 0}
                <div class="mt-2 space-y-1">
                  {#each schedule.escalationSteps.sort((a, b) => (a.stepOrder ?? 0) - (b.stepOrder ?? 0)) as step, idx (step.id ?? idx)}
                    <div
                      class="flex items-center gap-2 text-xs text-muted-foreground pl-4 border-l-2 border-muted py-1"
                    >
                      <span class="font-medium text-foreground">
                        Step {idx + 1}
                      </span>
                      {#if step.delaySeconds && step.delaySeconds > 0}
                        <span>
                          after {Math.round(step.delaySeconds / 60)}m
                        </span>
                      {:else}
                        <span>immediately</span>
                      {/if}
                      {#if step.channels && step.channels.length > 0}
                        <span class="mx-1">via</span>
                        {#each step.channels as channel, chIdx (channel.id ?? chIdx)}
                          <Badge variant="outline" class="text-xs">
                            {channel.channelType}
                            {#if channel.destinationLabel}
                              : {channel.destinationLabel}
                            {/if}
                          </Badge>
                        {/each}
                      {/if}
                    </div>
                  {/each}
                </div>
              {/if}
            </div>
          {/each}
        </div>
      {/if}

      <!-- Actions -->
      <Separator />
      <div class="flex items-center justify-end gap-2">
        <Button
          variant="outline"
          size="sm"
          onclick={onEdit}
        >
          <Pencil class="h-4 w-4 mr-2" />
          Edit Rule
        </Button>
        <AlertDialog.Root>
          <AlertDialog.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button
                {...props}
                variant="outline"
                size="sm"
                class="text-destructive"
              >
                <Trash2 class="h-4 w-4 mr-2" />
                Delete Rule
              </Button>
            {/snippet}
          </AlertDialog.Trigger>
          <AlertDialog.Content>
            <AlertDialog.Header>
              <AlertDialog.Title>Delete Alert Rule</AlertDialog.Title>
              <AlertDialog.Description>
                Are you sure you want to delete "{rule.name}"? This
                will also remove all associated schedules and
                escalation steps. This action cannot be undone.
              </AlertDialog.Description>
            </AlertDialog.Header>
            <AlertDialog.Footer>
              <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
              <AlertDialog.Action
                onclick={onDelete}
              >
                {#if isDeleting}
                  <Loader2 class="h-4 w-4 mr-2 animate-spin" />
                {/if}
                Delete
              </AlertDialog.Action>
            </AlertDialog.Footer>
          </AlertDialog.Content>
        </AlertDialog.Root>
      </div>
    </div>
  {/if}
</div>
