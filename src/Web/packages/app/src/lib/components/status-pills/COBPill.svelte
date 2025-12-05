<script lang="ts">
	import StatusPill from './StatusPill.svelte';
	import type { COBPillData, PillInfoItem, AlertLevel } from '$lib/types/status-pills';

	interface COBPillProps {
		data: COBPillData | null;
	}

	let { data }: COBPillProps = $props();

	/**
	 * Build info items for the popover
	 */
	const info = $derived.by((): PillInfoItem[] => {
		if (!data) return [];

		const items: PillInfoItem[] = [];

		// Last carbs info
		if (data.lastCarbs) {
			const when = new Date(data.lastCarbs.mills);
			const timeStr = when.toLocaleString([], {
				month: 'short',
				day: 'numeric',
				hour: '2-digit',
				minute: '2-digit'
			});
			const amount = `${data.lastCarbs.carbs}g`;
			items.push({ label: 'Last Carbs', value: `${amount} @ ${timeStr}` });

			// Food description if available
			if (data.lastCarbs.food) {
				items.push({ label: 'Food', value: data.lastCarbs.food });
			}

			// Notes if available
			if (data.lastCarbs.notes) {
				items.push({ label: 'Notes', value: data.lastCarbs.notes });
			}
		}

		// Source
		if (data.source) {
			items.push({ label: 'Source', value: data.source });
		}

		// Decay status
		if (data.isDecaying !== undefined) {
			items.push({
				label: 'Status',
				value: data.isDecaying ? 'Actively absorbing' : 'Waiting for absorption'
			});
		}

		return items;
	});

	const level = $derived<AlertLevel>(data?.level ?? 'none');
	const display = $derived(data?.display ?? '---g');
	const isStale = $derived(data?.isStale ?? false);
</script>

<StatusPill value={display} label="COB" {info} {level} {isStale} />
