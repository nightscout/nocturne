<script lang="ts">
	import StatusPill from './StatusPill.svelte';
	import type { SAGEPillData, PillInfoItem, AlertLevel } from '$lib/types/status-pills';

	interface SAGEPillProps {
		data: SAGEPillData | null;
		/** Maximum sensor life in hours (default 168 = 7 days for G6, 240 = 10 days for G7) */
		maxLifeHours?: number;
	}

	let { data, maxLifeHours = 240 }: SAGEPillProps = $props();

	/**
	 * Format time remaining
	 */
	function formatTimeRemaining(hours: number): string {
		if (hours <= 0) return 'Overdue';

		const days = Math.floor(hours / 24);
		const remainingHours = Math.round(hours % 24);

		if (days > 0) {
			return `${days}d ${remainingHours}h`;
		}
		return `${remainingHours}h`;
	}

	/**
	 * Format duration display
	 */
	function formatDuration(days: number, hours: number): string {
		if (days > 0) {
			return `${days} day${days !== 1 ? 's' : ''}, ${hours} hour${hours !== 1 ? 's' : ''}`;
		}
		const totalHours = days * 24 + hours;
		return `${totalHours} hour${totalHours !== 1 ? 's' : ''}`;
	}

	/**
	 * Build info items for the popover
	 */
	const info = $derived.by((): PillInfoItem[] => {
		if (!data) return [];

		const items: PillInfoItem[] = [];

		// Event type and insertion time
		const eventLabel = data.eventType === 'Sensor Change' ? 'Sensor Insert' : 'Sensor Start';
		if (data.treatmentDate) {
			const insertedDate = new Date(data.treatmentDate);
			items.push({
				label: eventLabel,
				value: insertedDate.toLocaleString([], {
					month: 'short',
					day: 'numeric',
					year: 'numeric',
					hour: '2-digit',
					minute: '2-digit'
				})
			});
		}

		// Duration
		if (data.days !== undefined && data.hours !== undefined) {
			items.push({
				label: 'Duration',
				value: formatDuration(data.days, data.hours)
			});
		}

		// Time remaining (calculated from maxLifeHours)
		const timeRemaining = data.timeRemaining ?? maxLifeHours - data.age;
		if (timeRemaining !== undefined) {
			const remainingStr = formatTimeRemaining(timeRemaining);
			const remainingClass =
				timeRemaining <= 0
					? 'text-red-600 font-bold'
					: timeRemaining <= 12
						? 'text-yellow-600'
						: '';
			items.push({
				label: 'Time Remaining',
				value: remainingClass ? `<span class="${remainingClass}">${remainingStr}</span>` : remainingStr
			});
		}

		// Sensor details
		if (data.transmitterId) {
			items.push({ label: '------------', value: '' });
			items.push({ label: 'Transmitter ID', value: data.transmitterId });
		}

		// Notes from treatment
		if (data.notes) {
			items.push({ label: '------------', value: '' });
			items.push({ label: 'Notes', value: data.notes });
		}

		// Sensor life info
		items.push({ label: '------------', value: '' });
		items.push({
			label: 'Sensor Life',
			value: `${Math.floor(maxLifeHours / 24)} days (${maxLifeHours}h)`
		});

		return items;
	});

	const level = $derived<AlertLevel>(data?.level ?? 'none');
	const display = $derived(data?.display ?? 'n/a');
	const isStale = $derived(data?.isStale ?? (!data?.treatmentDate));
</script>

<StatusPill value={display} label="SAGE" {info} {level} {isStale} />
