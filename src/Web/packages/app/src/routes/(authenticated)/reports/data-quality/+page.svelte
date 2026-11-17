<script lang="ts">
	import { resolve } from "$app/paths";
	import { Button } from '#lib/components/ui/button/index.js';
	import { Input } from '#lib/components/ui/input/index.js';
	import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '#lib/components/ui/card/index.js';

	import {
		getSuggestions as getCompressionLowSuggestions,
		triggerDetection as triggerCompressionLowDetection
	} from '#lib/api/generated/compressionLows.generated.remote.js';
	import { getRejectedUploads } from '#lib/api/generated/rejectedUploads.generated.remote.js';
	import { reportDescription } from '#lib/navigation/report-navigation.svelte.js';
	import { contextResource } from '#lib/hooks/resource-context.svelte.js';
	import ShieldCheck from '@lucide/svelte/icons/shield-check';
	import Clock from '@lucide/svelte/icons/clock';
	import ChevronRight from '@lucide/svelte/icons/chevron-right';
	import RefreshCw from '@lucide/svelte/icons/refresh-cw';
	import type { CompressionLowSuggestion } from '#lib/api/index.js';
	import FigureStrip from '#lib/components/reports/FigureStrip.svelte';
	import { setReportPrintMeta } from '#lib/components/reports/print/report-print.svelte.js';

	setReportPrintMeta(() => ({ period: { label: 'All recorded nights' } }));

	const suggestionsResource = contextResource(
		() => getCompressionLowSuggestions({}),
		{ errorTitle: 'Error loading data quality report' }
	);

	const suggestions = $derived(suggestionsResource.current ?? []);

	const rejectedUploads = getRejectedUploads({ limit: 1 });
	const rejectedCount = $derived(rejectedUploads.current?.pagination?.total ?? 0);

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

{#if suggestionsResource.current}
	<div class="@container space-y-6">
		<div class="flex items-center gap-3">
			<div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 print:hidden">
				<ShieldCheck class="h-5 w-5 text-primary" />
			</div>
			<div>
				<p class="text-muted-foreground print:hidden">Spot readings that can come from the sensor rather than your glucose</p>
				<h2 class="hidden text-lg font-semibold print:block">Compression lows by review status</h2>
			</div>
		</div>

		<FigureStrip
			figures={[
				{ label: 'Pending review', value: String(pendingCount) },
				{ label: 'Accepted', value: String(acceptedCount) },
				{ label: 'Dismissed', value: String(dismissedCount) }
			]}
		/>

		<section class="print:hidden" aria-labelledby="data-quality-categories">
			<h2 id="data-quality-categories" class="mb-3 text-lg font-semibold">Data quality categories</h2>
			<ul class="m-0 list-none divide-y divide-border border-y border-border p-0">
				<li>
					<a
						href={resolve('reports/data-quality/compression-lows')}
						class="group/link -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
					>
						<div class="min-w-0 flex-1">
							<div class="font-medium text-foreground">Compression lows</div>
							<div class="text-sm text-muted-foreground">{reportDescription("/reports/data-quality/compression-lows")}</div>

							<div
								class="mt-1 text-sm text-muted-foreground tabular-nums"
							>
								{acceptedCount} events{#if pendingCount > 0}
									· {pendingCount} pending
								{/if}
							</div>
						</div>
						<ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/link:translate-x-0.5" aria-hidden="true" />
					</a>
				</li>
				<li>
					<a
						href={resolve('reports/data-quality/sensor-integrity')}
						class="group/link -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
					>
						<div class="min-w-0 flex-1">
							<div class="font-medium text-foreground">Signal integrity</div>
							<div class="text-sm text-muted-foreground">
								{reportDescription("/reports/data-quality/sensor-integrity")}
							</div>
						</div>
						<ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/link:translate-x-0.5" aria-hidden="true" />
					</a>
				</li>
				<li>
					<a
						href={resolve('reports/data-quality/cgm-comparison')}
						class="group/link -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
					>
						<div class="min-w-0 flex-1">
							<div class="font-medium text-foreground">CGM comparison</div>
							<div class="text-sm text-muted-foreground">
								{reportDescription("/reports/data-quality/cgm-comparison")}
							</div>
						</div>
						<ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/link:translate-x-0.5" aria-hidden="true" />
					</a>
				</li>
				{#if rejectedUploads.current}
					<li>
						<a
							href={resolve('reports/data-quality/rejected-uploads')}
							class="group/link -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
						>
							<div class="min-w-0 flex-1">
								<div class="font-medium text-foreground">Rejected uploads</div>
								<div class="text-sm text-muted-foreground">{reportDescription("/reports/data-quality/rejected-uploads")}</div>

								<div
									class="mt-1 text-sm text-muted-foreground tabular-nums"
								>{rejectedCount} records</div>
							</div>

							<ChevronRight
								class="size-4 shrink-0 text-muted-foreground transition-transform group-hover/link:translate-x-0.5"
								aria-hidden="true"
							/>
						</a>
					</li>
				{/if}
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
								Accept each one as a false low from pressure on the sensor, or dismiss it as a real
								low. This is for your own record: your reports and statistics still include these
								readings.
							</p>
						</div>
					</div>
					<Button href="/reports/data-quality/compression-lows" variant="default" class="shrink-0">
						Review now
					</Button>
				</CardContent>
			</Card>
		{/if}

		<Card class="print:hidden">
			<CardHeader>
				<CardTitle class="text-base">Run detection</CardTitle>
				<CardDescription>
					Manually scan a date range for compression lows
				</CardDescription>
			</CardHeader>
			<CardContent>
				<div class="flex flex-wrap items-end gap-4">
					<div class="flex flex-col gap-1">
						<label for="start-date" class="text-sm text-muted-foreground">Start date</label>
						<Input
							id="start-date"
							type="date"
							bind:value={testStartDate}
							class="w-auto"
						/>
					</div>
					<div class="flex flex-col gap-1">
						<label for="end-date" class="text-sm text-muted-foreground">End date (optional)</label>
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
						Run detection
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
