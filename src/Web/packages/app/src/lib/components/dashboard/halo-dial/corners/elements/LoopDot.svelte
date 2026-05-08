<script lang="ts">
	interface Props {
		value: { status: string } | null;
	}

	const { value }: Props = $props();

	// Loop status uses chart-* tokens for green (closed) and yellow (limited) since
	// --success/--warning are not defined across themes. Add those tokens then swap.
	function colorFor(status: string | undefined): string {
		switch (status) {
			case "closed":
			case "looping":
				return "var(--chart-2)";
			case "open":
				return "var(--destructive)";
			case "limited":
				return "var(--chart-4)";
			default:
				return "var(--muted-foreground)";
		}
	}

	function labelFor(status: string | undefined): string {
		switch (status) {
			case "closed":
				return "Loop closed";
			case "looping":
				return "Loop looping";
			case "open":
				return "Loop open";
			case "limited":
				return "Loop limited";
			default:
				return "Loop status unknown";
		}
	}

	const color = $derived(colorFor(value?.status));
	const label = $derived(value === null ? "no-data" : value.status);
	const ariaLabel = $derived(value === null ? "Loop status no data" : labelFor(value.status));
</script>

<span
	class="inline-block size-2 rounded-full"
	style="background-color: {color};"
	data-testid="loop-dot"
	data-status={label}
	aria-label={ariaLabel}
></span>
