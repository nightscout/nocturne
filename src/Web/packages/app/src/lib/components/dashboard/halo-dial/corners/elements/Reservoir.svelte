<script lang="ts">
	import { Droplet } from "lucide-svelte";

	interface Props {
		value:
			| { units: number; percent: number; minutesRemaining: number }
			| null;
		options: { format: "units" | "percent" | "time-left" };
	}

	const { value, options }: Props = $props();

	function formatMinutes(mins: number): string {
		if (mins < 60) return `${Math.round(mins)}m`;
		const h = Math.floor(mins / 60);
		const m = Math.round(mins % 60);
		return m === 0 ? `${h}h` : `${h}h ${m}m`;
	}

	const text = $derived.by(() => {
		if (value === null) return "--";
		switch (options.format) {
			case "units":
				return `${value.units.toFixed(1)} U`;
			case "percent":
				return `${Math.round(value.percent)}%`;
			case "time-left":
				return formatMinutes(value.minutesRemaining);
		}
	});
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-foreground"
	style="font-family: var(--font-mono);"
	data-testid="reservoir"
	data-format={options.format}
>
	<Droplet
		class={value === null ? "size-3 opacity-40" : "size-3"}
		aria-hidden="true"
	/>
	<span>{text}</span>
</span>
