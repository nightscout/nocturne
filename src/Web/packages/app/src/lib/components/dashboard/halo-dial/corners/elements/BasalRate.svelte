<script lang="ts">
	import { Activity } from "lucide-svelte";

	interface Props {
		value: { rate: number; percent: number } | null;
		options: { format: "U/h" | "%" | "both" };
	}

	const { value, options }: Props = $props();

	const text = $derived.by(() => {
		if (value === null) return "--";
		const rate = `${value.rate.toFixed(2)} U/h`;
		const pct = `${Math.round(value.percent)}%`;
		switch (options.format) {
			case "U/h":
				return rate;
			case "%":
				return pct;
			case "both":
				return `${rate} (${pct})`;
		}
	});
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-foreground"
	style="font-family: var(--font-mono);"
	data-testid="basal-rate"
	data-format={options.format}
>
	<Activity
		class={value === null ? "size-3 opacity-40" : "size-3"}
		aria-hidden="true"
	/>
	<span>{text}</span>
</span>
