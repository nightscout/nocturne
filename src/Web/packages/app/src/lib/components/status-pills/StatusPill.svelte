<script lang="ts">
  import * as Popover from "$lib/components/ui/popover";
  import type { AlertLevel, PillInfoItem } from "$lib/types/status-pills";
  import { cn } from "$lib/utils";
  import { PlusCircle } from "lucide-svelte";

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

  /** Get pill styling based on alert level */
  const pillClasses = $derived.by(() => {
    const baseClasses =
      "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-sm font-medium transition-colors cursor-pointer select-none";

    const levelClasses: Record<AlertLevel, string> = {
      none: "bg-secondary text-secondary-foreground hover:bg-secondary/80",
      info: "bg-info/10 text-info hover:bg-info/20",
      warn: "bg-warning/10 text-warning hover:bg-warning/20",
      urgent:
        "bg-destructive/10 text-destructive hover:bg-destructive/20",
    };

    const staleClasses = isStale ? "opacity-60" : "";

    return cn(baseClasses, levelClasses[level], staleClasses, className);
  });

  /** Get label styling based on level */
  const labelClasses = $derived.by(() => {
    const baseClasses = "text-xs font-normal opacity-75";
    return cn(baseClasses);
  });
  let popoverOpen = $state(false);
</script>

{#if showPopover && info.length > 0}
  <Popover.Root bind:open={popoverOpen}>
    <Popover.Trigger class={pillClasses}>
      <span class={labelClasses}>{label}</span>
      <span>{value}</span>
      {#if isStale}
        <span class="text-xs opacity-50">?</span>
      {/if}
    </Popover.Trigger>
    <Popover.Content class="w-80 p-0" align="center" side="bottom">
      <div class="px-4 py-3 border-b border-border">
        <h4 class="font-semibold text-sm">{label}</h4>
        <p class="text-xs text-muted-foreground">{value}</p>
      </div>
      <div class="px-4 py-3 space-y-2 max-h-80 overflow-y-auto">
        {#each info as item}
          {#if item.label === "------------"}
            <hr class="border-border my-2" />
          {:else}
            <div class="flex justify-between items-start gap-2 text-sm">
              <span class="text-muted-foreground shrink-0">{item.label}</span>
              <span class="text-right font-medium wrap-break-word">
                {@html item.value}
              </span>
            </div>
          {/if}
        {/each}
      </div>
      {#if actionLabel && onAction}
        <div class="p-2 border-t border-border">
          <button
            class="flex w-full items-center rounded-sm px-2 py-1.5 text-sm outline-none hover:bg-accent hover:text-accent-foreground cursor-pointer"
            onclick={() => {
              onAction();
              popoverOpen = false;
            }}
          >
            <PlusCircle class="mr-2 h-4 w-4 flex items-center justify-center" />
            {actionLabel}
          </button>
        </div>
      {/if}
    </Popover.Content>
  </Popover.Root>
{:else}
  <div class={pillClasses}>
    <span class={labelClasses}>{label}</span>
    <span>{value}</span>
    {#if isStale}
      <span class="text-xs opacity-50">?</span>
    {/if}
  </div>
{/if}
