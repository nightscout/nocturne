<script lang="ts">
	import { Button } from '$lib/components/ui/button';
	import {
		Card,
		CardContent,
		CardDescription,
		CardHeader,
		CardTitle
	} from '$lib/components/ui/card';
	import {
		getSuggestions as getCompressionLowSuggestions,
		triggerDetection as triggerCompressionLowDetection
	} from '$api/generated/compressionLows.generated.remote';
	import { contextResource } from '$lib/hooks/resource-context.svelte';
	import ShieldCheck from 'lucide-svelte/icons/shield-check';
	import Activity from 'lucide-svelte/icons/activity';
	import Waves from 'lucide-svelte/icons/waves';
	import Clock from 'lucide-svelte/icons/clock';
	import Check from 'lucide-svelte/icons/check';
	import X from 'lucide-svelte/icons/x';
	import ChevronRight from 'lucide-svelte/icons/chevron-right';
	import RefreshCw from 'lucide-svelte/icons/refresh-cw';
	import type { CompressionLowSuggestion } from '$lib/api';

	// Create resource with automatic layout registration
	const suggestionsResource = contextResource(
		() => getCompressionLowSuggestions({}),
		{ errorTitle: 'Error Loading Data Quality Report' }
	);

	const suggestions = $derived(suggestionsResource.current ?? []);

	const pendingCount = $derived(
		suggestions.filter((s: CompressionLowSuggestion) => s.status?.toLowerCase() === 'pending')
			.length
	);

	const acceptedCount = $derived(
		suggestions.filter((s: CompressionLowSuggestion) => s.status?.toLowerCase() === 'accepted')
			.length
	);

	const dismissedCount = $derived(
		suggestions.filter((s: CompressionLowSuggestion) => s.status?.toLowerCase() === 'dismissed')
			.length
	);

	// Detection trigger state
	let testStartDate = $state('');
	let testEndDate = $state('');
	let isDetecting = $state(false);
	let detectionResult = $state<{
		totalSuggestionsCreated?: number;
		nightsProcessed?: number;
	} | null>(null);

	async function handleTriggerDetection() {
		if (!testStartDate) return;
		isDetecting = true;
		detectionResult = null;
		try {
			const result = await triggerCompressionLowDetection({
				startDate: testStartDate,
				endDate: testEndDate || testStartDate
			});
			detectionResult = result;
			suggestionsResource.refresh();
		} finally {
			isDetecting = false;
		}
	}
</script>

<svelte:head>
	<title>Data Quality - Nocturne</title>
</svelte:head>

{#if suggestionsResource.current}
	<div class="@container container mx-auto max-w-4xl space-y-6 p-3 @md:p-6">
		<!-- Header -->
		<div class="flex items-center gap-3">
			<div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10">
				<ShieldCheck class="h-5 w-5 text-primary" />
			</div>
			<div>
				<h1 class="text-2xl font-bold tracking-tight">Data Quality</h1>
				<p class="text-muted-foreground">Monitor and manage data exclusions</p>
			</div>
		</div>

		<!-- Summary Stats -->
		<div class="grid gap-4 @lg:grid-cols-3">
			<Card>
				<CardContent class="flex items-center gap-4 pt-6">
					<div
						class="flex h-12 w-12 items-center justify-center rounded-full bg-amber-100 dark:bg-amber-900/30"
					>
						<Clock class="h-6 w-6 text-amber-600 dark:text-amber-400" />
					</div>
					<div>
						<p class="text-2xl font-bold">{pendingCount}</p>
						<p class="text-sm text-muted-foreground">Pending Review</p>
					</div>
				</CardContent>
			</Card>
			<Card>
				<CardContent class="flex items-center gap-4 pt-6">
					<div
						class="flex h-12 w-12 items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30"
					>
						<Check class="h-6 w-6 text-green-600 dark:text-green-400" />
					</div>
					<div>
						<p class="text-2xl font-bold">{acceptedCount}</p>
						<p class="text-sm text-muted-foreground">Accepted</p>
					</div>
				</CardContent>
			</Card>
			<Card>
				<CardContent class="flex items-center gap-4 pt-6">
					<div
						class="flex h-12 w-12 items-center justify-center rounded-full bg-slate-100 dark:bg-slate-800"
					>
						<X class="h-6 w-6 text-slate-600 dark:text-slate-400" />
					</div>
					<div>
						<p class="text-2xl font-bold">{dismissedCount}</p>
						<p class="text-sm text-muted-foreground">Dismissed</p>
					</div>
				</CardContent>
			</Card>
		</div>

		<!-- Data Quality Categories: in-page navigation to sub-reports -->
		<div class="space-y-4 print:hidden">
			<h2 class="text-lg font-semibold">Data Quality Categories</h2>

			<!-- Compression Lows Card -->
			<a href="/reports/data-quality/compression-lows" class="block">
				<Card class="transition-colors hover:bg-muted/50">
					<CardHeader class="pb-3">
						<div class="flex items-center justify-between">
							<div class="flex items-center gap-3">
								<div
									class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
								>
									<Activity class="h-5 w-5 text-primary" />
								</div>
								<div>
									<CardTitle class="text-base">Compression Lows</CardTitle>
									<CardDescription>
										Falsely low readings from sleeping on sensor
									</CardDescription>
								</div>
							</div>
							<div class="flex items-center gap-3">
								{#if pendingCount > 0}
									<span
										class="rounded-full bg-amber-100 px-3 py-1 text-sm font-medium text-amber-700 dark:bg-amber-900/30 dark:text-amber-400"
									>
										{pendingCount} pending
									</span>
								{/if}
								<ChevronRight class="h-5 w-5 text-muted-foreground" />
							</div>
						</div>
					</CardHeader>
					<CardContent class="pt-0">
						<div class="flex gap-6 text-sm text-muted-foreground">
							<span>{acceptedCount} events</span>
						</div>
					</CardContent>
				</Card>
			</a>

			<!-- Signal Integrity Card -->
			<a href="/reports/data-quality/sensor-integrity" class="block">
				<Card class="transition-colors hover:bg-muted/50">
					<CardHeader class="pb-3">
						<div class="flex items-center justify-between">
							<div class="flex items-center gap-3">
								<div
									class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
								>
									<Waves class="h-5 w-5 text-primary" />
								</div>
								<div>
									<CardTitle class="text-base">Signal Integrity</CardTitle>
									<CardDescription>
										Windows where readings oscillate in a way that is unlikely to be physiologic
									</CardDescription>
								</div>
							</div>
							<ChevronRight class="h-5 w-5 text-muted-foreground" />
						</div>
					</CardHeader>
				</Card>
			</a>
		</div>

		<!-- Quick Actions: review CTA -->
		{#if pendingCount > 0}
			<Card
				class="border-amber-200 bg-amber-50/50 print:hidden dark:border-amber-900/50 dark:bg-amber-900/10"
			>
				<CardContent class="flex flex-col gap-3 pt-6 @lg:flex-row @lg:items-center @lg:justify-between">
					<div class="flex items-center gap-3">
						<Clock class="h-5 w-5 text-amber-600 dark:text-amber-400" />
						<div>
							<p class="font-medium">
								You have {pendingCount} item{pendingCount !== 1 ? 's' : ''} waiting for review
							</p>
							<p class="text-sm text-muted-foreground">
								Review detected compression lows to improve your statistics accuracy
							</p>
						</div>
					</div>
					<Button href="/reports/data-quality/compression-lows" variant="default" class="shrink-0">
						Review Now
					</Button>
				</CardContent>
			</Card>
		{/if}

		<!-- Run Detection: date-range controls + trigger button -->
		<Card class="print:hidden">
			<CardHeader>
				<CardTitle class="text-base">Run Detection</CardTitle>
				<CardDescription>
					Manually scan a date range for compression lows
				</CardDescription>
			</CardHeader>
			<CardContent>
				<div class="flex flex-wrap items-end gap-4">
					<div class="flex flex-col gap-1">
						<label for="start-date" class="text-sm text-muted-foreground">Start Date</label>
						<input
							id="start-date"
							type="date"
							bind:value={testStartDate}
							class="rounded border bg-background px-3 py-2"
						/>
					</div>
					<div class="flex flex-col gap-1">
						<label for="end-date" class="text-sm text-muted-foreground">End Date (optional)</label>
						<input
							id="end-date"
							type="date"
							bind:value={testEndDate}
							min={testStartDate}
							class="rounded border bg-background px-3 py-2"
						/>
					</div>
					<Button onclick={handleTriggerDetection} disabled={isDetecting || !testStartDate}>
						<RefreshCw class="mr-2 h-4 w-4 {isDetecting ? 'animate-spin' : ''}" />
						Run Detection
					</Button>
				</div>
				{#if detectionResult}
					<p class="mt-4 text-sm text-muted-foreground">
						Found {detectionResult.totalSuggestionsCreated} compression low(s) across {detectionResult.nightsProcessed}
						night(s)
					</p>
				{/if}
			</CardContent>
		</Card>
	</div>
{/if}
