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
  }

  let {
    value = $bindable(undefined),
    placeholder = "e.g., 7x24 or 168",
    class: className,
    id,
    disabled = false,
    onchange,
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
   * Parse duration expression and return hours Supports:
   *
   * - Plain numbers: 168 → 168
   * - Multiplication: 7x24, 7*24, 7 x 24, 7 * 24 → 168
   * - Days: 7d, 7 days, 7 day → 168
   * - Weeks: 1w, 1 week, 1 weeks → 168
   */
  function parseExpression(expr: string): number | null {
    if (!expr || expr.trim() === "") return null;

    const trimmed = expr.trim().toLowerCase();

    // Plain number
    if (/^\d+(\.\d+)?$/.test(trimmed)) {
      return parseFloat(trimmed);
    }

    // Multiplication expression: 7x24, 7*24, 7 x 24, 7 * 24
    const multiplyMatch = trimmed.match(
      /^(\d+(?:\.\d+)?)\s*[x*×]\s*(\d+(?:\.\d+)?)$/
    );
    if (multiplyMatch) {
      return parseFloat(multiplyMatch[1]) * parseFloat(multiplyMatch[2]);
    }

    // Days: 7d, 7 days, 7day
    const daysMatch = trimmed.match(/^(\d+(?:\.\d+)?)\s*d(?:ays?)?$/);
    if (daysMatch) {
      return parseFloat(daysMatch[1]) * 24;
    }

    // Weeks: 1w, 1 week, 1weeks
    const weeksMatch = trimmed.match(/^(\d+(?:\.\d+)?)\s*w(?:eeks?)?$/);
    if (weeksMatch) {
      return parseFloat(weeksMatch[1]) * 24 * 7;
    }

    // Hours explicit: 168h, 168 hours
    const hoursMatch = trimmed.match(/^(\d+(?:\.\d+)?)\s*h(?:ours?)?$/);
    if (hoursMatch) {
      return parseFloat(hoursMatch[1]);
    }

    return null;
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

  // Computed display value
  const computedHours = $derived(value !== undefined ? value : null);
  const showComputed = $derived(
    computedHours !== null &&
      inputValue.trim() !== "" &&
      inputValue.trim() !== String(computedHours)
  );
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
        </ul>
      </Tooltip.Content>
    </Tooltip.Root>
  </div>
  {#if showComputed}
    <p class="text-sm text-muted-foreground px-1">
      = <span class="font-medium">{computedHours}</span>
      hours
      {#if computedHours && computedHours >= 24}
        <span class="text-muted-foreground/70">
          ({(computedHours / 24).toFixed(1)} days)
        </span>
      {/if}
    </p>
  {/if}
  {#if parseError}
    <p class="text-sm text-destructive px-1">
      Invalid format. Try: 168, 7x24, 7d, or 1w
    </p>
  {/if}
</div>
