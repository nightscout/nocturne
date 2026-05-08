<script lang="ts">
	import { Battery as BatteryIcon } from "lucide-svelte";

	interface Props {
		value:
			| { percent: number; voltage?: number; minutesRemaining?: number }
			| null;
		options: { format: "percent" | "voltage" | "time-left" };
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
			case "percent":
				return `${Math.round(value.percent)}%`;
			case "voltage":
				return value.voltage === undefined
					? "--"
					: `${value.voltage.toFixed(2)}V`;
			case "time-left":
				return value.minutesRemaining === undefined
					? "--"
					: formatMinutes(value.minutesRemaining);
		}
	});
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-foreground"
	style="font-family: var(--font-mono);"
	data-testid="battery"
	data-format={options.format}
>
	<BatteryIcon
		class={value === null ? "size-3 opacity-40" : "size-3"}
		aria-hidden="true"
	/>
	<span>{text}</span>
</span>
