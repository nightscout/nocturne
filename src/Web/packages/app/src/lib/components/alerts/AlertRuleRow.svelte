<script lang="ts">
  import type { AlertRuleResponse } from "$api-clients";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Switch } from "$lib/components/ui/switch";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import {
    Loader2,
    Pencil,
    Trash2,
    Zap,
    MoreHorizontal,
  } from "lucide-svelte";
  import {
    summarizeCondition,
    type SummarizeContext,
  } from "./summarizeCondition";
  import { severity } from "./severity";
  import { nodeFromApi } from "./types";
  import { findChannelMeta } from "./channelMeta";

  interface Props {
    rule: AlertRuleResponse;
    isToggling: boolean;
    isDeleting: boolean;
    isTesting: boolean;
    onToggleEnabled: () => void;
    onEdit: () => void;
    onDelete: () => void;
    onTestFire: () => void;
    /** Lookup map for `alert_state` references in the rule chip. */
    resolveAlertName?: (id: string) => string | undefined;
  }

  let {
    rule,
    isToggling,
    isDeleting,
    isTesting,
    onToggleEnabled,
    onEdit,
    onDelete,
    onTestFire,
    resolveAlertName,
  }: Props = $props();

  // Reconstruct the editor-side ConditionNode from the API's flat
  // (conditionType, conditionParams) pair so we can render a human chip via
  // summarizeCondition. Defensive against malformed rows: if reconstruction
  // fails we fall back to the raw discriminator.
  let chip = $derived(
    (() => {
      const node = nodeFromApi(rule.conditionType, rule.conditionParams);
      const ctx: SummarizeContext = { resolveAlertName };
      return node ? summarizeCondition(node, ctx) : (rule.conditionType ?? "");
    })()
  );

  let severityClass = $derived(severity(rule.severity, "dot"));
</script>

<div
  class="flex items-center gap-3 rounded-md border bg-background px-4 py-3 {!rule.isEnabled
    ? 'opacity-60'
    : ''}"
>
  <!-- Severity dot -->
  <span
    class="h-2.5 w-2.5 shrink-0 rounded-full {severityClass}"
    aria-label="Severity: {rule.severity ?? 'warning'}"
  ></span>

  <!-- Identity + condition summary chip -->
  <div class="min-w-0 flex-1">
    <div class="flex items-center gap-2">
      <button
        type="button"
        class="text-sm font-semibold truncate hover:underline"
        onclick={onEdit}
      >
        {rule.name ?? "(unnamed)"}
      </button>
      {#if !rule.isEnabled}
        <Badge variant="secondary" class="text-[10px]">Disabled</Badge>
      {/if}
    </div>
    <div class="truncate text-xs text-muted-foreground" title={chip}>
      {chip || "No condition configured"}
    </div>
  </div>

  <!-- Channel icons -->
  {#if rule.channels && rule.channels.length > 0}
    <div class="hidden items-center gap-1 sm:flex" aria-label="Channels">
      {#each rule.channels.slice(0, 4) as ch (ch.id)}
        {@const meta = findChannelMeta(ch.channelType)}
        <span
          class="grid h-6 w-6 place-items-center rounded bg-muted text-muted-foreground overflow-hidden"
          title={ch.destinationLabel ?? ch.channelType ?? ""}
        >
          {#if meta?.logo}
            <img src={meta.logo} alt="" class="h-4 w-4 object-contain" />
          {:else if meta?.icon}
            {@const G = meta.icon}
            <G class="h-3 w-3" />
          {/if}
        </span>
      {/each}
      {#if rule.channels.length > 4}
        <span class="text-xs text-muted-foreground">
          +{rule.channels.length - 4}
        </span>
      {/if}
    </div>
  {/if}

  <!-- Per-row actions -->
  <div class="flex items-center gap-1 shrink-0">
    <Button
      type="button"
      variant="ghost"
      size="icon"
      class="h-8 w-8"
      onclick={onTestFire}
      disabled={isTesting || !rule.isEnabled}
      title="Test fire — sends a real notification"
    >
      {#if isTesting}
        <Loader2 class="h-4 w-4 animate-spin" />
      {:else}
        <Zap class="h-4 w-4" />
      {/if}
    </Button>
    <Switch
      checked={rule.isEnabled ?? false}
      onCheckedChange={onToggleEnabled}
      disabled={isToggling}
      aria-label="Enable rule"
    />
    <DropdownMenu.Root>
      <DropdownMenu.Trigger>
        {#snippet child({ props })}
          <Button
            {...props}
            type="button"
            variant="ghost"
            size="icon"
            class="h-8 w-8"
            aria-label="Row actions"
          >
            <MoreHorizontal class="h-4 w-4" />
          </Button>
        {/snippet}
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="end">
        <DropdownMenu.Item onclick={onEdit}>
          <Pencil class="h-4 w-4 mr-2" /> Edit
        </DropdownMenu.Item>
        <DropdownMenu.Separator />
        <AlertDialog.Root>
          <AlertDialog.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <DropdownMenu.Item
                {...props}
                class="text-destructive"
                onSelect={(e: Event) => e.preventDefault()}
              >
                <Trash2 class="h-4 w-4 mr-2" /> Delete
              </DropdownMenu.Item>
            {/snippet}
          </AlertDialog.Trigger>
          <AlertDialog.Content>
            <AlertDialog.Header>
              <AlertDialog.Title>Delete "{rule.name}"?</AlertDialog.Title>
              <AlertDialog.Description>
                This rule will stop firing immediately. Existing alert history
                is preserved. This action cannot be undone.
              </AlertDialog.Description>
            </AlertDialog.Header>
            <AlertDialog.Footer>
              <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
              <AlertDialog.Action onclick={onDelete} disabled={isDeleting}>
                {#if isDeleting}
                  <Loader2 class="h-4 w-4 mr-2 animate-spin" />
                {/if}
                Delete
              </AlertDialog.Action>
            </AlertDialog.Footer>
          </AlertDialog.Content>
        </AlertDialog.Root>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
  </div>
</div>
