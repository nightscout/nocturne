<script lang="ts">
	import type { DataQualitySettings } from '$lib/api/generated/nocturne-api-client';
	import { getUiSettings, saveDataQualitySettings } from '$api/ui-settings.remote';
	import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '$lib/components/ui/card';
	import { Switch } from '$lib/components/ui/switch';
	import { Label } from '$lib/components/ui/label';
	import { Select, SelectContent, SelectItem, SelectTrigger } from '$lib/components/ui/select';
	import { Moon, Activity, AlertCircle, Globe, Weight, ChevronRight } from 'lucide-svelte';
	import SettingsPageSkeleton from '$lib/components/settings/SettingsPageSkeleton.svelte';
	import { resolve } from '$app/paths';
	import { toast } from 'svelte-sonner';

	// Read via .current rather than an {#await} block: consuming a remote query
	// through its thenable reads the hydration cache and throws
	// hydratable_missing_but_required during hydration.
	const settingsQuery = getUiSettings();
	const dataQuality = $derived(settingsQuery.current?.dataQuality);

	const bedtimeHour = $derived(dataQuality?.sleepSchedule?.bedtimeHour ?? 23);
	const wakeTimeHour = $derived(dataQuality?.sleepSchedule?.wakeTimeHour ?? 7);
	const detectionEnabled = $derived(dataQuality?.compressionLowDetection?.enabled ?? true);
	const excludeFromStatistics = $derived(
		dataQuality?.compressionLowDetection?.excludeFromStatistics ?? true
	);

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
		} catch {
			toast.error('Could not save. Check your connection and try again.');
			await settingsQuery.refresh();
		}
	}
</script>

<svelte:head>
	<title>Data Quality - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
	<!-- Header -->
	<div class="flex items-center gap-3">
		<div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
			<Activity class="h-6 w-6 text-primary" />
		</div>
		<div>
			<h1 class="text-2xl font-bold tracking-tight">Data Quality</h1>
			<p class="text-muted-foreground">Configure how Nocturne handles data quality and analysis</p>
		</div>
	</div>

	{#if settingsQuery.loading}
		<SettingsPageSkeleton cardCount={2} />
	{:else if settingsQuery.error}
		<Card class="border-destructive">
			<CardContent class="flex items-center gap-3 py-6">
				<AlertCircle class="h-5 w-5 text-destructive" />
				<div>
					<p class="font-medium">Failed to load settings</p>
					<p class="text-sm text-muted-foreground">{settingsQuery.error.message}</p>
				</div>
			</CardContent>
		</Card>
	{:else if dataQuality}
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
							value={String(wakeTimeHour)}
							onValueChange={(value) =>
								save({ sleepSchedule: { wakeTimeHour: parseInt(value) } })}
						>
							<SelectTrigger class="w-full">
								{formatHour(wakeTimeHour)}
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
						checked={detectionEnabled}
						onCheckedChange={(checked: boolean) =>
							save({ compressionLowDetection: { enabled: checked } })}
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
						checked={excludeFromStatistics}
						onCheckedChange={(checked: boolean) =>
							save({ compressionLowDetection: { excludeFromStatistics: checked } })}
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

		<!-- Timezone History (lives under Data Quality — correct timestamps are a data-quality concern) -->
		<a href={resolve('/settings/timezone')} class="group block">
			<Card class="transition-colors hover:border-primary/40 hover:bg-muted/40">
				<CardContent class="flex items-center gap-4 p-4">
					<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
						<Globe class="h-5 w-5 text-primary" />
					</div>
					<div class="min-w-0 flex-1">
						<p class="font-medium">Timezone History</p>
						<p class="text-sm text-muted-foreground">
							Where you've lived and travelled, for correct timestamps.
						</p>
					</div>
					<ChevronRight
						class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
					/>
				</CardContent>
			</Card>
		</a>

		<!-- Weight History (lives under Data Quality — same pattern as Timezone History) -->
		<a href={resolve('/settings/weight')} class="group block">
			<Card class="transition-colors hover:border-primary/40 hover:bg-muted/40">
				<CardContent class="flex items-center gap-4 p-4">
					<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
						<Weight class="h-5 w-5 text-primary" />
					</div>
					<div class="min-w-0 flex-1">
						<p class="font-medium">Weight History</p>
						<p class="text-sm text-muted-foreground">
							Your recorded weights over time.
						</p>
					</div>
					<ChevronRight
						class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
					/>
				</CardContent>
			</Card>
		</a>
	{/if}
</div>
