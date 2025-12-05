<script lang="ts">
	import StatusPill from './StatusPill.svelte';
	import type { IOBPillData, PillInfoItem, AlertLevel } from '$lib/types/status-pills';

	interface IOBPillProps {
		data: IOBPillData | null;
		units?: string;
	}

	let { data, units = 'mmol/L' }: IOBPillProps = $props();

	/**
	 * Build info items for the popover
	 */
	const info = $derived.by((): PillInfoItem[] => {
		if (!data) return [];

		const items: PillInfoItem[] = [];

		// Last bolus info
		if (data.lastBolus) {
			const when = new Date(data.lastBolus.mills).toLocaleTimeString([], {
				hour: '2-digit',
				minute: '2-digit'
			});
			const amount = `${data.lastBolus.insulin.toFixed(2)}U`;
			items.push({ label: 'Last Bolus', value: `${amount} @ ${when}` });

			if (data.lastBolus.notes) {
				items.push({ label: 'Notes', value: data.lastBolus.notes });
			}
		}

		// Basal IOB
		if (data.basalIob !== undefined) {
			items.push({ label: 'Basal IOB', value: `${data.basalIob.toFixed(2)}U` });
		}

		// Activity (insulin impact on BG)
		if (data.activity !== undefined && data.activity !== 0) {
			const activityDisplay = data.activity.toFixed(4);
			items.push({ label: 'Activity', value: `${activityDisplay} ${units}/5min` });
		}

		// Source information
		if (data.source) {
			items.push({ label: 'Source', value: data.source });
		}

		if (data.device) {
			items.push({ label: 'Device', value: data.device });
		}

		return items;
	});

	const level = $derived<AlertLevel>(data?.level ?? 'none');
	const display = $derived(data?.display ?? '---U');
	const isStale = $derived(data?.isStale ?? false);
</script>

<StatusPill value={display} label="IOB" {info} {level} {isStale} />
