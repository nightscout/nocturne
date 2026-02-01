<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Button } from "$lib/components/ui/button";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { HelpCircle } from "lucide-svelte";
  import { cn } from "$lib/utils";

  interface Props {
    /** The computed hours value (bindable) */
    value?: number | undefined;
    /** Placeholder text */
    placeholder?: string;
    /** Additional CSS classes */
    class?: string;
    /** ID for the input */
    id?: string;
    /** Disabled state */
    disabled?: boolean;
    /** Callback when value changes (alternative to bind:value) */
    onchange?: (value: number | undefined) => void;
    /** Tracker mode for display formatting */
    mode?: "Duration" | "Event";
    /** Lifespan hours for negative threshold validation (Duration mode only) */
    lifespanHours?: number | undefined;
  }

  let {
    value = $bindable(undefined),
    placeholder = "e.g., 7x24 or 168",
    class: className,
    id,
    disabled = false,
    onchange,
    mode = "Duration",
    lifespanHours,
  }: Props = $props();

  // Internal state for the text input
  let inputValue = $state("");
  let parseError = $state(false);

  // Initialize inputValue from value prop
  $effect(() => {
    if (value !== undefined && inputValue === "") {
      inputValue = String(value);
    }
  });

  /**
   * Parse duration expression and return hours. Supports:
   *
   * - Plain numbers: 168 → 168, -24 → -24
   * - Multiplication: 7x24, 7*24, 7 x 24, 7 * 24 → 168
   * - Days: 7d, 7 days, 7 day → 168, -2d → -48
   * - Weeks: 1w, 1 week, 1 weeks → 168, -1w → -168
   * - Hours explicit: 168h, 168 hours, -24h → -24
   */
  function parseExpression(expr: string): number | null {
    if (!expr || expr.trim() === "") return null;

    const trimmed = expr.trim().toLowerCase();

    // Check for negative prefix
    const isNegative = trimmed.startsWith("-");
    const absExpr = isNegative ? trimmed.slice(1) : trimmed;

    let result: number | null = null;

    // Plain number
    if (/^\d+(\.\d+)?$/.test(absExpr)) {
      result = parseFloat(absExpr);
    }
    // Multiplication expression: 7x24, 7*24, 7 x 24, 7 * 24
    else {
      const multiplyMatch = absExpr.match(
        /^(\d+(?:\.\d+)?)\s*[x*×]\s*(\d+(?:\.\d+)?)$/
      );
      if (multiplyMatch) {
        result = parseFloat(multiplyMatch[1]) * parseFloat(multiplyMatch[2]);
      }
    }

    // Days: 7d, 7 days, 7day
    if (result === null) {
      const daysMatch = absExpr.match(/^(\d+(?:\.\d+)?)\s*d(?:ays?)?$/);
      if (daysMatch) {
        result = parseFloat(daysMatch[1]) * 24;
      }
    }

    // Weeks: 1w, 1 week, 1weeks
    if (result === null) {
      const weeksMatch = absExpr.match(/^(\d+(?:\.\d+)?)\s*w(?:eeks?)?$/);
      if (weeksMatch) {
        result = parseFloat(weeksMatch[1]) * 24 * 7;
      }
    }

    // Hours explicit: 168h, 168 hours
    if (result === null) {
      const hoursMatch = absExpr.match(/^(\d+(?:\.\d+)?)\s*h(?:ours?)?$/);
      if (hoursMatch) {
        result = parseFloat(hoursMatch[1]);
      }
    }

    if (result === null) return null;
    return isNegative ? -result : result;
  }

  function handleInput() {
    const parsed = parseExpression(inputValue);
    if (parsed !== null) {
      value = parsed;
      onchange?.(parsed);
      parseError = false;
    } else if (inputValue.trim() === "") {
      value = undefined;
      onchange?.(undefined);
      parseError = false;
    } else {
      parseError = true;
    }
  }

  /**
   * Format hours with smart day/hour display
   * <= 48 hours: show hours only
   * < 360 hours (15 days): show days + hours
   * >= 360 hours: show days only
   */
  function formatHours(hours: number): string {
    const absHours = Math.abs(hours);

    if (absHours <= 48) {
      return `${absHours} hours`;
    }

    const days = Math.floor(absHours / 24);
    const remainingHours = Math.floor(absHours % 24);

    if (absHours >= 360) {
      // 15+ days: show days only
      return `${days} days`;
    }

    // Show days + hours (omit hours if 0)
    if (remainingHours === 0) {
      return `${days} days`;
    }
    return `${days} days ${remainingHours} hours`;
  }

  // Computed display value
  const computedHours = $derived(value !== undefined ? value : null);
  const showComputed = $derived(
    computedHours !== null &&
      inputValue.trim() !== "" &&
      inputValue.trim() !== String(computedHours)
  );

  // Validation for negative thresholds in Duration mode
  const exceedsLifespan = $derived(
    mode === "Duration" &&
      value !== undefined &&
      value < 0 &&
      lifespanHours !== undefined &&
      Math.abs(value) >= lifespanHours
  );

  // Effective hours for Duration mode negative thresholds
  const effectiveHours = $derived(() => {
    if (value === undefined) return null;
    if (mode === "Event") return null; // Event mode doesn't need this
    if (value >= 0) return null; // Positive values don't need effective calculation
    if (lifespanHours === undefined) return null;
    return lifespanHours + value;
  });
</script>

<div class="space-y-1">
  <div class="relative">
    <Input
      {id}
      {disabled}
      bind:value={inputValue}
      oninput={handleInput}
      {placeholder}
      class={cn("pr-10", parseError && "border-destructive", className)}
      aria-invalid={parseError}
    />
    <Tooltip.Root>
      <Tooltip.Trigger>
        {#snippet child({ props })}
          <Button
            {...props}
            variant="ghost"
            size="icon"
            type="button"
            class="absolute right-1 top-1/2 -translate-y-1/2 h-7 w-7 text-muted-foreground hover:text-foreground"
            tabindex={-1}
          >
            <HelpCircle class="h-4 w-4" />
            <span class="sr-only">Show supported formats</span>
          </Button>
        {/snippet}
      </Tooltip.Trigger>
      <Tooltip.Content side="top" class="max-w-xs">
        <p class="font-medium mb-1">Supported formats:</p>
        <ul class="text-sm space-y-0.5">
          <li>
            <code class="bg-muted px-1 rounded">168</code>
            — plain hours
          </li>
          <li>
            <code class="bg-muted px-1 rounded">7x24</code>
            or
            <code class="bg-muted px-1 rounded">7*24</code>
            — multiplication
          </li>
          <li>
            <code class="bg-muted px-1 rounded">7d</code>
            or
            <code class="bg-muted px-1 rounded">7 days</code>
            — days to hours
          </li>
          <li>
            <code class="bg-muted px-1 rounded">1w</code>
            or
            <code class="bg-muted px-1 rounded">1 week</code>
            — weeks to hours
          </li>
          <li>
            <code class="bg-muted px-1 rounded">-24</code>
            or
            <code class="bg-muted px-1 rounded">-2d</code>
            — before expiration/event
          </li>
        </ul>
      </Tooltip.Content>
    </Tooltip.Root>
  </div>
  {#if showComputed && computedHours !== null}
    <p class="text-sm text-muted-foreground px-1">
      {#if mode === "Event"}
        {#if computedHours < 0}
          = <span class="font-medium">{formatHours(computedHours)}</span> before event
        {:else}
          = <span class="font-medium">{formatHours(computedHours)}</span> after event
        {/if}
      {:else if computedHours < 0 && lifespanHours !== undefined}
        = <span class="font-medium">{formatHours(effectiveHours() ?? 0)}</span>
        <span class="text-muted-foreground/70">
          ({formatHours(Math.abs(computedHours))} before expiration)
        </span>
      {:else}
        = <span class="font-medium">{formatHours(computedHours)}</span>
      {/if}
    </p>
  {/if}
  {#if exceedsLifespan}
    <p class="text-sm text-destructive px-1">
      Exceeds tracker lifespan
    </p>
  {:else if parseError}
    <p class="text-sm text-destructive px-1">
      Invalid format. Try: 168, 7x24, 7d, or 1w
    </p>
  {/if}
</div>
