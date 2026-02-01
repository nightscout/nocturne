<script lang="ts">
	import { TextareaAutosize } from "runed";
  import { cn, type WithElementRef, type WithoutChildren } from "$lib/utils";
  import type { HTMLTextareaAttributes } from "svelte/elements";

  let {
    ref = $bindable(null),
    value = $bindable(),
    class: className,
    ...restProps
  }: WithoutChildren<WithElementRef<HTMLTextareaAttributes>> = $props();

	let el = $state<HTMLTextAreaElement>(null!);

	// Sync ref with el for external access
	$effect(() => {
		if (el) ref = el;
	});

	new TextareaAutosize({
		element: () => el,
		input: () => String(value ?? ""),
		styleProp: "minHeight"
	});
</script>

<textarea
  bind:this={el}
  data-slot="textarea"
  class={cn(
    "border-input placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive dark:bg-input/30 shadow-xs flex min-h-16 w-full rounded-md border bg-transparent px-3 py-2 text-base outline-none transition-[color,box-shadow] focus-visible:ring-[3px] disabled:cursor-not-allowed disabled:opacity-50 md:text-sm resize-none",
    className
  )}
  bind:value
  {...restProps}
></textarea>
