<script lang="ts">
	import { resolve } from "$app/paths";
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
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
	import Clock from 'lucide-svelte/icons/clock';
	import ChevronRight from 'lucide-svelte/icons/chevron-right';
	import RefreshCw from 'lucide-svelte/icons/refresh-cw';
	import type { CompressionLowSuggestion } from '$lib/api';
	import FigureStrip from '$lib/components/reports/FigureStrip.svelte';
	import { setReportPrintMeta } from '$lib/components/reports/print/report-print.svelte';

	setReportPrintMeta(() => ({ period: { label: 'All recorded nights' } }));

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
		<div class="flex items-center gap-3">
			<div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 print:hidden">
				<ShieldCheck class="h-5 w-5 text-primary" />
			</div>
			<div>
				<h1 class="text-2xl font-bold tracking-tight print:hidden">Data Quality</h1>
				<p class="text-muted-foreground print:hidden">Monitor and manage data exclusions</p>
				<h2 class="hidden text-lg font-semibold print:block">Compression lows by review status</h2>
			</div>
		</div>

		<FigureStrip
			figures={[
				{ label: 'Pending Review', value: String(pendingCount) },
				{ label: 'Accepted', value: String(acceptedCount) },
				{ label: 'Dismissed', value: String(dismissedCount) }
			]}
		/>

		<section class="print:hidden" aria-labelledby="data-quality-categories">
			<h2 id="data-quality-categories" class="mb-3 text-lg font-semibold">Data Quality Categories</h2>
			<ul class="m-0 list-none divide-y divide-border border-y border-border p-0">
				<li>
					<a
						href={resolve("/reports/data-quality/compression-lows")}
						class="group/link -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
					>
						<div class="min-w-0 flex-1">
							<div class="font-medium text-foreground">Compression Lows</div>
							<div class="text-sm text-muted-foreground">
								Falsely low readings from sleeping on sensor
							</div>
							<div class="mt-1 text-sm text-muted-foreground tabular-nums">
								{acceptedCount} events{#if pendingCount > 0} · {pendingCount} pending{/if}
							</div>
						</div>
						<ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/link:translate-x-0.5" aria-hidden="true" />
					</a>
				</li>
				<li>
					<a
						href={resolve("/reports/data-quality/sensor-integrity")}
						class="group/link -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
					>
						<div class="min-w-0 flex-1">
							<div class="font-medium text-foreground">Signal Integrity</div>
							<div class="text-sm text-muted-foreground">
								Windows where readings oscillate in a way that is unlikely to be physiologic
							</div>
						</div>
						<ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/link:translate-x-0.5" aria-hidden="true" />
					</a>
				</li>
				<li>
					<a
						href={resolve("/reports/data-quality/cgm-comparison")}
						class="group/link -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
					>
						<div class="min-w-0 flex-1">
							<div class="font-medium text-foreground">CGM Comparison</div>
							<div class="text-sm text-muted-foreground">
								How far apart two sensors run over the same window
							</div>
						</div>
						<ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/link:translate-x-0.5" aria-hidden="true" />
					</a>
				</li>
			</ul>
		</section>

		{#if pendingCount > 0}
			<Card variant="warning" class="print:hidden">
				<CardContent class="flex flex-col gap-3 pt-6 @lg:flex-row @lg:items-center @lg:justify-between">
					<div class="flex items-center gap-3">
						<Clock class="h-5 w-5 text-warning" />
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
						<Input
							id="start-date"
							type="date"
							bind:value={testStartDate}
							class="w-auto"
						/>
					</div>
					<div class="flex flex-col gap-1">
						<label for="end-date" class="text-sm text-muted-foreground">End Date (optional)</label>
						<Input
							id="end-date"
							type="date"
							bind:value={testEndDate}
							min={testStartDate}
							class="w-auto"
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
