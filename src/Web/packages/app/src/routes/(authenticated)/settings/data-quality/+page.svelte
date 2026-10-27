<script lang="ts">
  import ShieldCheck from "@lucide/svelte/icons/shield-check";
	import type { DataQualitySettings } from '#lib/api/generated/nocturne-api-client.js';
	import { getUiSettings } from '#lib/api/ui-settings.remote.js';
	import { saveDataQualitySettings } from '#lib/api/generated/uiSettings.generated.remote.js';
	import { remoteErrorMessage } from '#lib/api/remote-error.js';
	import { describeSubmitError } from '#lib/forms/submit-error.js';
	import { SETTINGS_LOAD_FAILED } from '#lib/api/ui-settings-messages.js';
	import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '#lib/components/ui/card/index.js';
	import { Switch } from '#lib/components/ui/switch/index.js';
	import { Label } from '#lib/components/ui/label/index.js';
	import { Select, SelectContent, SelectItem, SelectTrigger } from '#lib/components/ui/select/index.js';
	import Moon from '@lucide/svelte/icons/moon';
	import Activity from '@lucide/svelte/icons/activity';
	import AlertCircle from '@lucide/svelte/icons/circle-alert';
	import SettingsPageSkeleton from '#lib/components/settings/SettingsPageSkeleton.svelte';
	import DataMaintenanceCard from '#lib/components/settings/DataMaintenanceCard.svelte';
	import { toast } from 'svelte-sonner';

	// Read via .current rather than an {#await} block: consuming a remote query
	// through its thenable reads the hydration cache and throws
	// hydratable_missing_but_required during hydration.
	const settingsQuery = getUiSettings();
	const dataQuality = $derived(settingsQuery.current?.dataQuality);

	const bedtimeHour = $derived(dataQuality?.sleepSchedule?.bedtimeHour ?? 23);
	const wakeTimeHour = $derived(dataQuality?.sleepSchedule?.wakeTimeHour ?? 7);
	const detectionEnabled = $derived(dataQuality?.compressionLowDetection?.enabled ?? true);

	// Hour options for bedtime (evening hours)
	const bedtimeHours = [
		{ value: 20, label: '8:00 PM' },
		{ value: 21, label: '9:00 PM' },
		{ value: 22, label: '10:00 PM' },
		{ value: 23, label: '11:00 PM' },
		{ value: 0, label: '12:00 AM' },
		{ value: 1, label: '1:00 AM' }
	];

	// Hour options for wake time (morning hours)
	const wakeTimeHours = [
		{ value: 4, label: '4:00 AM' },
		{ value: 5, label: '5:00 AM' },
		{ value: 6, label: '6:00 AM' },
		{ value: 7, label: '7:00 AM' },
		{ value: 8, label: '8:00 AM' },
		{ value: 9, label: '9:00 AM' },
		{ value: 10, label: '10:00 AM' }
	];

	function formatHour(hour: number): string {
		const found = [...bedtimeHours, ...wakeTimeHours].find((h) => h.value === hour);
		return found?.label ?? `${hour}:00`;
	}

	/**
	 * Persists the whole section — the API stores it as one document, so the patch
	 * merges over the loaded values (keeping e.g. the sleep-schedule timezone).
	 */
	async function save(patch: Partial<DataQualitySettings>) {
		const current = dataQuality ?? {};
		try {
			await saveDataQualitySettings({
				...current,
				sleepSchedule: { ...current.sleepSchedule, ...patch.sleepSchedule },
				compressionLowDetection: {
					...current.compressionLowDetection,
					...patch.compressionLowDetection
				}
			});
		} catch (err) {
			toast.error(describeSubmitError(err, 'Could not save. Check your connection and try again.'));
		}
		await settingsQuery.refresh();
	}
</script>

<svelte:head>
	<title>Data quality - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
	<!-- Header -->
	<div class="flex items-center gap-3">
		<div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
			<ShieldCheck class="size-6 text-primary" />
		</div>
		<div>
			<h1 class="text-2xl font-bold tracking-tight">Data quality</h1>
			<p class="text-muted-foreground">Configure how Nocturne handles data quality and analysis</p>
		</div>
	</div>

	{#if settingsQuery.loading}
		<SettingsPageSkeleton cardCount={2} />
	{:else if settingsQuery.error}
		<Card variant="destructive">
			<CardContent class="flex items-center gap-3 py-6">
				<AlertCircle class="h-5 w-5 text-destructive" />
				<p class="font-medium">
					{remoteErrorMessage(settingsQuery.error, SETTINGS_LOAD_FAILED)}
				</p>
			</CardContent>
		</Card>
	{:else if dataQuality}
		<!-- Sleep Schedule -->
		<Card>
			<CardHeader>
				<CardTitle class="flex items-center gap-2">
					<Moon class="h-5 w-5" />
					Sleep schedule
				</CardTitle>
				<CardDescription>
					Your typical sleep times are used for overnight analysis features
				</CardDescription>
			</CardHeader>
			<CardContent class="space-y-6">
				<div class="grid gap-4 @sm:grid-cols-2">
					<div class="space-y-2">
						<Label>Typical bedtime</Label>
						<Select
							type="single"
							value={String(bedtimeHour)}
							onValueChange={(value) =>
								save({ sleepSchedule: { bedtimeHour: parseInt(value) } })}
						>
							<SelectTrigger class="w-full">
								{formatHour(bedtimeHour)}
							</SelectTrigger>
							<SelectContent>
								{#each bedtimeHours as hour (hour.value)}
									<SelectItem value={String(hour.value)}>{hour.label}</SelectItem>
								{/each}
							</SelectContent>
						</Select>
					</div>
					<div class="space-y-2">
						<Label>Typical wake time</Label>
						<Select
							type="single"
							value={String(wakeTimeHour)}
							onValueChange={(value) =>
								save({ sleepSchedule: { wakeTimeHour: parseInt(value) } })}
						>
							<SelectTrigger class="w-full">
								{formatHour(wakeTimeHour)}
							</SelectTrigger>
							<SelectContent>
								{#each wakeTimeHours as hour (hour.value)}
									<SelectItem value={String(hour.value)}>{hour.label}</SelectItem>
								{/each}
							</SelectContent>
						</Select>
					</div>
				</div>
			</CardContent>
		</Card>

		<!-- Compression Low Detection -->
		<Card>
			<CardHeader>
				<CardTitle class="flex items-center gap-2">
					<Activity class="h-5 w-5" />
					Compression low detection
				</CardTitle>
				<CardDescription>
					Automatically detect potential compression lows during sleep
				</CardDescription>
			</CardHeader>
			<CardContent class="space-y-6">
				<div class="flex items-center justify-between">
					<div class="space-y-0.5">
						<Label>Enable automatic detection</Label>
						<p class="text-sm text-muted-foreground">
							Nocturne will analyze your overnight data and notify you when potential compression
							lows are detected
						</p>
					</div>
					<Switch
						checked={detectionEnabled}
						onCheckedChange={(checked: boolean) =>
							save({ compressionLowDetection: { enabled: checked } })}
					/>
				</div>

				<div class="rounded-lg border border-muted bg-muted/50 p-4">
					<p class="text-sm text-muted-foreground">
						Compression lows are falsely low CGM readings caused by sleeping on your sensor. When
						Nocturne detects them, it notifies you to review and confirm them. Reviewing keeps a
						record for you. It does not remove the readings from your reports or statistics.
					</p>
				</div>
			</CardContent>
		</Card>

	{/if}

	<!-- Outside the branches above: these tools read no UI settings, so a
	     settings load failure must not take them away. -->
	<DataMaintenanceCard />
</div>
