<script lang="ts">
  import * as ToggleGroup from "$lib/components/ui/toggle-group";

  type Op = "and" | "or";

  interface Props {
    value: Op;
    /**
     * Compact form used for nested groups — narrower padding, no surrounding
     * copy.
     */
    size?: "default" | "compact";
    onChange: (next: Op) => void;
    /**
     * Override label text — defaults to "all of" / "any of" (or "all" / "any"
     * when compact).
     */
    allLabel?: string;
    anyLabel?: string;
  }

  let {
    value,
    size = "default",
    onChange,
    allLabel,
    anyLabel,
  }: Props = $props();

  let allText = $derived(allLabel ?? (size === "compact" ? "all" : "all of"));
  let anyText = $derived(anyLabel ?? (size === "compact" ? "any" : "any of"));
  let itemClass = $derived(
    size === "compact"
      ? "h-6 px-1.5 text-xs data-[state=on]:bg-muted"
      : "h-7 px-2 text-xs data-[state=on]:bg-muted"
  );
</script>

<ToggleGroup.Root
  type="single"
  {value}
  onValueChange={(next) => {
    if (next === "and" || next === "or") onChange(next);
  }}
  class="rounded-md border bg-background p-0.5 font-medium"
>
  <ToggleGroup.Item
    value="and"
    class={itemClass}
    aria-label="All conditions must hold"
  >
    {allText}
  </ToggleGroup.Item>
  <ToggleGroup.Item
    value="or"
    class={itemClass}
    aria-label="Any condition is enough"
  >
    {anyText}
  </ToggleGroup.Item>
</ToggleGroup.Root>
