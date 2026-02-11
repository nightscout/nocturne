<script lang="ts">
	import { getSettingsStore } from '$lib/stores/settings-store.svelte';
	import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '$lib/components/ui/card';
	import { Switch } from '$lib/components/ui/switch';
	import { Label } from '$lib/components/ui/label';
	import { Select, SelectContent, SelectItem, SelectTrigger } from '$lib/components/ui/select';
	import { Moon, Activity, AlertCircle } from 'lucide-svelte';
	import SettingsPageSkeleton from '$lib/components/settings/SettingsPageSkeleton.svelte';

	const store = getSettingsStore();

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
</script>

<svelte:head>
	<title>Data Quality - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-3xl space-y-6 p-6">
	<!-- Header -->
	<div>
		<h1 class="text-2xl font-bold tracking-tight">Data Quality</h1>
		<p class="text-muted-foreground">Configure how Nocturne handles data quality and analysis</p>
	</div>

	{#if store.isLoading}
		<SettingsPageSkeleton cardCount={2} />
	{:else if store.hasError}
		<Card class="border-destructive">
			<CardContent class="flex items-center gap-3 py-6">
				<AlertCircle class="h-5 w-5 text-destructive" />
				<div>
					<p class="font-medium">Failed to load settings</p>
					<p class="text-sm text-muted-foreground">{store.error}</p>
				</div>
			</CardContent>
		</Card>
	{:else if store.dataQuality}
		<!-- Sleep Schedule -->
		<Card>
			<CardHeader>
				<CardTitle class="flex items-center gap-2">
					<Moon class="h-5 w-5" />
					Sleep Schedule
				</CardTitle>
				<CardDescription>
					Your typical sleep times are used for overnight analysis features
				</CardDescription>
			</CardHeader>
			<CardContent class="space-y-6">
				<div class="grid gap-4 sm:grid-cols-2">
					<div class="space-y-2">
						<Label>Typical bedtime</Label>
						<Select
							type="single"
							value={String(store.dataQuality.sleepSchedule?.bedtimeHour ?? 23)}
							onValueChange={(value) => {
								if (store.dataQuality?.sleepSchedule) {
									store.dataQuality.sleepSchedule.bedtimeHour = parseInt(value);
									store.markChanged();
								}
							}}
						>
							<SelectTrigger class="w-full">
								{formatHour(store.dataQuality.sleepSchedule?.bedtimeHour ?? 23)}
							</SelectTrigger>
							<SelectContent>
								{#each bedtimeHours as hour}
									<SelectItem value={String(hour.value)}>{hour.label}</SelectItem>
								{/each}
							</SelectContent>
						</Select>
					</div>
					<div class="space-y-2">
						<Label>Typical wake time</Label>
						<Select
							type="single"
							value={String(store.dataQuality.sleepSchedule?.wakeTimeHour ?? 7)}
							onValueChange={(value) => {
								if (store.dataQuality?.sleepSchedule) {
									store.dataQuality.sleepSchedule.wakeTimeHour = parseInt(value);
									store.markChanged();
								}
							}}
						>
							<SelectTrigger class="w-full">
								{formatHour(store.dataQuality.sleepSchedule?.wakeTimeHour ?? 7)}
							</SelectTrigger>
							<SelectContent>
								{#each wakeTimeHours as hour}
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
					Compression Low Detection
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
						checked={store.dataQuality.compressionLowDetection?.enabled ?? true}
						onCheckedChange={(checked) => {
							if (store.dataQuality?.compressionLowDetection) {
								store.dataQuality.compressionLowDetection.enabled = checked;
								store.markChanged();
							}
						}}
					/>
				</div>

				<div class="flex items-center justify-between">
					<div class="space-y-0.5">
						<Label>Exclude from statistics</Label>
						<p class="text-sm text-muted-foreground">
							Don't include accepted compression lows when calculating Time in Range and other
							statistics
						</p>
					</div>
					<Switch
						checked={store.dataQuality.compressionLowDetection?.excludeFromStatistics ?? true}
						onCheckedChange={(checked) => {
							if (store.dataQuality?.compressionLowDetection) {
								store.dataQuality.compressionLowDetection.excludeFromStatistics = checked;
								store.markChanged();
							}
						}}
					/>
				</div>

				<div class="rounded-lg border border-muted bg-muted/50 p-4">
					<p class="text-sm text-muted-foreground">
						Compression lows are falsely low CGM readings caused by sleeping on your sensor. When
						detected, you'll be notified to review and confirm them.
					</p>
				</div>
			</CardContent>
		</Card>
	{/if}
</div>
