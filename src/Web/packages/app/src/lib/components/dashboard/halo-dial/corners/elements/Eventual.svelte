<script lang="ts">
	import { Target } from "lucide-svelte";

	interface Props {
		value: { mgdl: number; minutesAhead: number } | null;
		options: { format: "value" | "in-time-value" };
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
		const v = `${Math.round(value.mgdl)}`;
		switch (options.format) {
			case "value":
				return v;
			case "in-time-value":
				return `in ${formatMinutes(value.minutesAhead)} ${v}`;
		}
	});
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-foreground"
	style="font-family: var(--font-mono);"
	data-testid="eventual"
	data-format={options.format}
>
	<Target
		class={value === null ? "size-3 opacity-40" : "size-3"}
		aria-hidden="true"
	/>
	<span>{text}</span>
</span>
