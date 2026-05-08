<script lang="ts">
	import { Radio } from "lucide-svelte";

	interface Props {
		value: { startedAtMs: number; expiryMs?: number } | null;
		options: { format: "days" | "days+hours" | "until-expiry" };
	}

	const { value, options }: Props = $props();

	function formatAge(ms: number, withHours: boolean): string {
		const totalMin = Math.max(0, Math.floor(ms / 60000));
		const days = Math.floor(totalMin / (60 * 24));
		const hours = Math.floor((totalMin % (60 * 24)) / 60);
		if (!withHours) return `${days}d`;
		return `${days}d ${hours}h`;
	}

	function formatRemaining(ms: number): string {
		if (ms <= 0) return "expired";
		const totalMin = Math.floor(ms / 60000);
		const days = Math.floor(totalMin / (60 * 24));
		const hours = Math.floor((totalMin % (60 * 24)) / 60);
		if (days > 0) return `${days}d ${hours}h left`;
		return `${hours}h left`;
	}

	const text = $derived.by(() => {
		if (value === null) return "--";
		const now = Date.now();
		switch (options.format) {
			case "days":
				return formatAge(now - value.startedAtMs, false);
			case "days+hours":
				return formatAge(now - value.startedAtMs, true);
			case "until-expiry":
				if (value.expiryMs === undefined) return "--";
				return formatRemaining(value.expiryMs - now);
		}
	});
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-foreground"
	style="font-family: var(--font-mono);"
	data-testid="sensor-age"
	data-format={options.format}
>
	<Radio
		class={value === null ? "size-3 opacity-40" : "size-3"}
		aria-hidden="true"
	/>
	<span>{text}</span>
</span>
