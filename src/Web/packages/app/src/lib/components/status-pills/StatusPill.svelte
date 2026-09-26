<script lang="ts">
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import type { AlertLevel, PillInfoItem } from "$lib/types/status-pills";
  import { cn } from "$lib/utils";
  import { PlusCircle, TriangleAlert, History } from "lucide-svelte";

  interface StatusPillProps {
    /** Display value shown in the pill */
    value: string;
    /** Label for the pill */
    label: string;
    /** Array of info items for popover */
    info?: PillInfoItem[];
    /** Alert level for styling */
    level?: AlertLevel;
    /** Whether the data is stale/outdated */
    isStale?: boolean;
    /** Additional CSS classes */
    class?: string;
    /** Whether to show the popover on click */
    showPopover?: boolean;
    /** Label for the action button in popover */
    actionLabel?: string;
    /** Callback for the action button */
    onAction?: () => void;
  }

  let {
    value,
    label,
    info = [],
    level = "none",
    isStale = false,
    class: className,
    showPopover = true,
    actionLabel,
    onAction,
  }: StatusPillProps = $props();

  // eslint-disable-next-line shadcn/require-static-classes -- this is the status item itself, and PopoverTrigger renders unstyled.
  const pillClasses = $derived(
    cn(
      "inline-flex flex-col items-start rounded-md px-3 py-1 text-left whitespace-nowrap transition-colors cursor-pointer select-none hover:bg-accent/50",
      isStale && "opacity-60",
      className
    )
  );

  // Severity is urgency, read as text; warn and urgent also carry an icon so the level is never colour alone.
  const valueTone: Record<AlertLevel, string> = {
    none: "",
    info: "text-severity-info",
    warn: "text-severity-warn",
    urgent: "text-severity-urgent",
  };
  const labelClasses = "text-xs text-muted-foreground";
  let popoverOpen = $state(false);
</script>

{#snippet item()}
  <span class={labelClasses}>{label}</span>
  <span class="flex items-center gap-1 text-sm font-medium tabular-nums {valueTone[level]}">
    {#if level === "warn" || level === "urgent"}
      <TriangleAlert class="size-3.5" aria-hidden="true" />
      <span class="sr-only">{level === "urgent" ? "Urgent:" : "Warning:"}</span>
    {/if}
    {value}
    {#if isStale}
      <History class="size-3 text-muted-foreground" aria-hidden="true" />
      <span class="sr-only">(not current)</span>
    {/if}
  </span>
{/snippet}

{#if showPopover && info.length > 0}
  <Popover.Root bind:open={popoverOpen}>
    <Popover.Trigger class={pillClasses}>
      {@render item()}
    </Popover.Trigger>
    <Popover.Content class="w-80 p-0" align="center" side="bottom">
      <div class="px-4 py-3 border-b border-border">
        <h4 class="font-semibold text-sm">{label}</h4>
        <p class="text-xs text-muted-foreground">{value}</p>
      </div>
      <div class="px-4 py-3 space-y-2 max-h-80 overflow-y-auto">
        {#each info as item, i (i)}
          {#if item.label === "------------"}
            <hr class="border-border my-2" />
          {:else}
            <div class="flex justify-between items-start gap-2 text-sm">
              <span class="text-muted-foreground shrink-0">{item.label}</span>
              <span
                class={cn(
                  "text-right font-medium wrap-break-word",
                  item.tone === "destructive" && "text-destructive"
                )}
              >
                {#if item.lead}<b>{item.lead}</b>{/if}{item.value}
              </span>
            </div>
          {/if}
        {/each}
      </div>
      {#if actionLabel && onAction}
        <div class="p-2 border-t border-border">
          <Button
            variant="ghost"
            size="sm"
            class="w-full justify-start"
            onclick={() => {
              onAction();
              popoverOpen = false;
            }}
          >
            <PlusCircle class="h-4 w-4" />
            {actionLabel}
          </Button>
        </div>
      {/if}
    </Popover.Content>
  </Popover.Root>
{:else}
  <div class={pillClasses}>
    {@render item()}
  </div>
{/if}
