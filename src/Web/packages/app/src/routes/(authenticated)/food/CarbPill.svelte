<script lang="ts">
	interface Props {
		value: number;
		large?: boolean;
	}

	const { value, large = false }: Props = $props();
</script>

<span
	class="inline-flex items-baseline gap-1 whitespace-nowrap rounded-full border border-carbs/22 bg-carbs/12 text-(--carbs-strong) {large
		? 'px-3 py-1 text-sm'
		: 'px-2.5 py-0.5 text-xs'}"
>
	<span class="tabular-nums {large ? 'text-base font-bold' : 'text-sm font-bold'}">{value}</span>
	<span>g carbs</span>
</span>
