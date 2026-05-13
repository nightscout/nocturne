<script lang="ts">
	import { Button } from '$lib/components/ui/button';
	import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import {
		Select,
		SelectContent,
		SelectItem,
		SelectTrigger
	} from '$lib/components/ui/select';
	import {
		getSuggestions as getCompressionLowSuggestions,
		getSuggestion as getCompressionLowSuggestion,
		acceptSuggestion as acceptCompressionLow,
		dismissSuggestion as dismissCompressionLow,
		deleteSuggestion as deleteCompressionLow,
		triggerDetection as triggerCompressionLowDetection
	} from '$api/generated/compressionLows.generated.remote';
	import { contextResource } from '$lib/hooks/resource-context.svelte';
	import { createChartDataEngine } from '$lib/components/dashboard/glucose-chart/engine/chart-data-engine.svelte';
	import GlucoseChartShell from '$lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte';
	import GlucoseTrack from '$lib/components/dashboard/glucose-chart/tracks/GlucoseTrack.svelte';
	import BasalTrack from '$lib/components/dashboard/glucose-chart/tracks/BasalTrack.svelte';
	import IobCobTrack from '$lib/components/dashboard/glucose-chart/tracks/IobCobTrack.svelte';
	import ThresholdRules from '$lib/components/dashboard/glucose-chart/tracks/ThresholdRules.svelte';
	import ChartTooltip from '$lib/components/dashboard/glucose-chart/ChartTooltip.svelte';
	import Check from 'lucide-svelte/icons/check';
	import X from 'lucide-svelte/icons/x';
	import Clock from 'lucide-svelte/icons/clock';
	import Trash2 from 'lucide-svelte/icons/trash-2';
	import RefreshCw from 'lucide-svelte/icons/refresh-cw';
	import AlertTriangle from 'lucide-svelte/icons/triangle-alert';
	import History from 'lucide-svelte/icons/history';
	import ArrowLeft from 'lucide-svelte/icons/arrow-left';
	import { time } from '$lib/utils/formatting';
	import type { CompressionLowSuggestion } from '$lib/api';

	// Create resource with automatic layout registration - load ALL suggestions
	const suggestionsResource = contextResource(
		() => getCompressionLowSuggestions({}),
		{ errorTitle: 'Error Loading Compression Low History' }
	);

	const suggestions = $derived(suggestionsResource.current ?? []);

	let statusFilter = $state<string>('all');
	let selectedSuggestions = $state<Set<string>>(new Set());
	let activeSuggestion = $state<string | null>(null);
	let lastClickedIndex = $state<number>(-1);
	let suggestionDetail = $state<Awaited<ReturnType<typeof getCompressionLowSuggestion>> | null>(
		null
	);
	let brushDomain = $state<[Date, Date] | null>(null);
	let isLoading = $state(false);
	let testStartDate = $state('');
	let testEndDate = $state('');
	let detectionResult = $state<{
		totalSuggestionsCreated?: number;
		nightsProcessed?: number;
	} | null>(null);

	const filteredSuggestions = $derived.by(() => {
		if (statusFilter === 'all') return suggestions;
		return suggestions.filter(
			(s: CompressionLowSuggestion) => s.status?.toLowerCase() === statusFilter.toLowerCase()
		);
	});

	const pendingCount = $derived(
		suggestions.filter((s: CompressionLowSuggestion) => s.status?.toLowerCase() === 'pending')
			.length
	);

	const selectionCount = $derived(selectedSuggestions.size);
	const isBulkMode = $derived(selectionCount > 1);

	// Auto-select first suggestion when list loads or filter changes
	$effect(() => {
		if (filteredSuggestions.length > 0 && !activeSuggestion) {
			const first = filteredSuggestions[0];
			if (first?.id) {
				loadSuggestionDetail(first.id);
			}
		}
	});

	function handleItemClick(suggestion: CompressionLowSuggestion, index: number, event: MouseEvent) {
		if (!suggestion.id) return;

		if (event.shiftKey && lastClickedIndex >= 0) {
			const start = Math.min(lastClickedIndex, index);
			const end = Math.max(lastClickedIndex, index);
			const newSelection = new Set(selectedSuggestions);
			for (let i = start; i <= end; i++) {
				const id = filteredSuggestions[i]?.id;
				if (id) newSelection.add(id);
			}
			selectedSuggestions = newSelection;
		} else {
			selectedSuggestions = new Set([suggestion.id]);
		}

		lastClickedIndex = index;
		loadSuggestionDetail(suggestion.id);
	}

	async function loadSuggestionDetail(id: string) {
		activeSuggestion = id;
		if (selectedSuggestions.size === 0) {
			selectedSuggestions = new Set([id]);
		}
		suggestionDetail = await getCompressionLowSuggestion(id);
		if (suggestionDetail && suggestionDetail.suggestion?.startMills && suggestionDetail.suggestion?.endMills) {
			brushDomain = [new Date(suggestionDetail.suggestion.startMills), new Date(suggestionDetail.suggestion.endMills)];
		}
	}

	async function handleAccept() {
		if (selectedSuggestions.size === 0) return;
		isLoading = true;
		try {
			const ids = [...selectedSuggestions];
			const firstSelectedIndex = filteredSuggestions.findIndex((s) => s.id === ids[0]);
			for (const id of ids) {
				if (id === activeSuggestion && brushDomain) {
					await acceptCompressionLow({
						id,
						request: {
							startMills: brushDomain[0].getTime(),
							endMills: brushDomain[1].getTime()
						}
					});
				} else {
					const s = suggestions.find((s) => s.id === id);
					if (s?.startMills && s?.endMills) {
						await acceptCompressionLow({
							id,
							request: { startMills: s.startMills, endMills: s.endMills }
						});
					}
				}
			}
			suggestionsResource.refresh();
			selectedSuggestions = new Set();
			selectNextSuggestion(firstSelectedIndex);
		} finally {
			isLoading = false;
		}
	}

	async function handleDismiss() {
		if (selectedSuggestions.size === 0) return;
		isLoading = true;
		try {
			const ids = [...selectedSuggestions];
			const firstSelectedIndex = filteredSuggestions.findIndex((s) => s.id === ids[0]);
			for (const id of ids) {
				await dismissCompressionLow(id);
			}
			suggestionsResource.refresh();
			selectedSuggestions = new Set();
			selectNextSuggestion(firstSelectedIndex);
		} finally {
			isLoading = false;
		}
	}

	async function handleDelete() {
		if (selectedSuggestions.size === 0) return;
		isLoading = true;
		try {
			const ids = [...selectedSuggestions];
			const firstSelectedIndex = filteredSuggestions.findIndex((s) => s.id === ids[0]);
			for (const id of ids) {
				await deleteCompressionLow(id);
			}
			suggestionsResource.refresh();
			selectedSuggestions = new Set();
			selectNextSuggestion(firstSelectedIndex);
		} finally {
			isLoading = false;
		}
	}

	function selectNextSuggestion(previousIndex: number) {
		if (filteredSuggestions.length === 0) {
			activeSuggestion = null;
			suggestionDetail = null;
			brushDomain = null;
			lastClickedIndex = -1;
			return;
		}
		const nextIndex = Math.min(previousIndex, filteredSuggestions.length - 1);
		const nextSuggestion = filteredSuggestions[nextIndex];
		if (nextSuggestion?.id) {
			lastClickedIndex = nextIndex;
			selectedSuggestions = new Set([nextSuggestion.id]);
			loadSuggestionDetail(nextSuggestion.id);
		}
	}

	async function handleTriggerDetection() {
		if (!testStartDate) return;
		isLoading = true;
		detectionResult = null;
		try {
			const result = await triggerCompressionLowDetection({
				startDate: testStartDate,
				endDate: testEndDate || testStartDate
			});
			detectionResult = result;
			suggestionsResource.refresh();
		} finally {
			isLoading = false;
		}
	}

	function getStatusIcon(status: string | undefined) {
		switch (status?.toLowerCase()) {
			case 'accepted':
				return Check;
			case 'dismissed':
				return X;
			default:
				return Clock;
		}
	}

	function getStatusColor(status: string | undefined): string {
		switch (status?.toLowerCase()) {
			case 'accepted':
				return 'bg-green-100 text-green-600 dark:bg-green-900/30 dark:text-green-400';
			case 'dismissed':
				return 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400';
			default:
				return 'bg-amber-100 text-amber-600 dark:bg-amber-900/30 dark:text-amber-400';
		}
	}

	function getConfidenceLabel(confidence: number): string {
		if (confidence >= 0.75) return 'High';
		if (confidence >= 0.6) return 'Medium';
		return 'Low';
	}

	function getConfidenceVariant(confidence: number): 'default' | 'secondary' | 'outline' {
		if (confidence >= 0.75) return 'default';
		if (confidence >= 0.6) return 'secondary';
		return 'outline';
	}


	function formatNightOf(nightOf: string | Date): string {
		const date = nightOf instanceof Date ? nightOf : new Date(nightOf);
		const nextDay = new Date(date);
		nextDay.setDate(nextDay.getDate() + 1);
		const dateStr = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
		const nextDayStr = nextDay.toLocaleDateString(undefined, { day: 'numeric', year: 'numeric' });
		return `Night of ${dateStr}-${nextDayStr}`;
	}

	const chartDateRange = $derived.by(() => {
		const entries = suggestionDetail?.entries;
		if (!entries || entries.length === 0) return null;
		const times = entries.filter((e) => e.mills != null).map((e) => e.mills!);
		if (times.length === 0) return null;
		return {
			from: new Date(Math.min(...times)),
			to: new Date(Math.max(...times))
		};
	});

	function handleSelectionChange(domain: [Date, Date] | null) {
		brushDomain = domain;
	}

	const isPending = $derived(suggestionDetail?.suggestion?.status?.toLowerCase() === 'pending');
	const DetailStatusIcon = $derived(getStatusIcon(suggestionDetail?.suggestion?.status));
</script>

<svelte:head>
	<title>Compression Lows - Nocturne</title>
</svelte:head>

{#if suggestionsResource.current}
	<div class="container mx-auto space-y-6">
		<div class="flex items-center justify-between">
			<div class="flex items-center gap-4">
				<Button href="/reports/data-quality" variant="ghost" size="icon">
					<ArrowLeft class="h-4 w-4" />
				</Button>
				<div>
					<h1 class="text-2xl font-bold">Compression Lows</h1>
					<p class="text-muted-foreground">
						{#if pendingCount > 0}
							{pendingCount} pending review
						{:else}
							Review history and manage exclusions
						{/if}
					</p>
				</div>
			</div>
			<div class="flex items-center gap-2">
				<span class="text-sm text-muted-foreground">Status:</span>
				<Select
					type="single"
					value={statusFilter}
					onValueChange={(value) => {
						statusFilter = value;
						activeSuggestion = null;
						selectedSuggestions = new Set();
						suggestionDetail = null;
						lastClickedIndex = -1;
					}}
				>
					<SelectTrigger class="w-32">
						{statusFilter === 'all'
							? 'All'
							: statusFilter.charAt(0).toUpperCase() + statusFilter.slice(1)}
					</SelectTrigger>
					<SelectContent>
						<SelectItem value="all">All</SelectItem>
						<SelectItem value="pending">Pending</SelectItem>
						<SelectItem value="accepted">Accepted</SelectItem>
						<SelectItem value="dismissed">Dismissed</SelectItem>
					</SelectContent>
				</Select>
			</div>
		</div>

		{#if suggestions.length === 0}
			<Card>
				<CardContent class="py-12 text-center">
					<History class="mx-auto mb-4 h-12 w-12 text-muted-foreground" />
					<h2 class="mb-2 text-lg font-semibold">No compression lows detected yet</h2>
					<p class="mb-4 text-muted-foreground">
						When compression lows are detected during your sleep, they will appear here.
					</p>
					<div class="flex flex-col items-center gap-4">
						<div class="flex items-center gap-2">
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
								<label for="end-date" class="text-sm text-muted-foreground"
									>End Date (optional)</label
								>
								<input
									id="end-date"
									type="date"
									bind:value={testEndDate}
									min={testStartDate}
									class="rounded border bg-background px-3 py-2"
								/>
							</div>
							<Button
								onclick={handleTriggerDetection}
								disabled={isLoading || !testStartDate}
								class="mt-5"
							>
								<RefreshCw class="mr-2 h-4 w-4 {isLoading ? 'animate-spin' : ''}" />
								Run Detection
							</Button>
						</div>
						{#if detectionResult}
							<p class="text-sm text-muted-foreground">
								Found {detectionResult.totalSuggestionsCreated} compression low(s) across {detectionResult.nightsProcessed}
								night(s)
							</p>
						{/if}
					</div>
				</CardContent>
			</Card>
		{:else if filteredSuggestions.length === 0}
			<Card>
				<CardContent class="py-12 text-center">
					<AlertTriangle class="mx-auto mb-4 h-12 w-12 text-muted-foreground" />
					<h2 class="mb-2 text-lg font-semibold">No matching results</h2>
					<p class="text-muted-foreground">Try changing your filter criteria.</p>
				</CardContent>
			</Card>
		{:else}
			<div class="grid gap-6 lg:grid-cols-3">
				<!-- Suggestion List -->
				<div class="max-h-[600px] space-y-2 overflow-y-auto pr-2">
					{#each filteredSuggestions as suggestion, index (suggestion.id)}
						{@const StatusIcon = getStatusIcon(suggestion.status)}
						{@const isSelected = suggestion.id ? selectedSuggestions.has(suggestion.id) : false}
						{@const isActive = suggestion.id === activeSuggestion}
						<button
							type="button"
							class="w-full text-left"
							onclick={(e) => handleItemClick(suggestion, index, e)}
						>
							<div
								class="flex items-center justify-between rounded-lg border p-3 transition-colors hover:bg-muted/50 {isActive
									? 'ring-2 ring-primary'
									: isSelected
										? 'ring-2 ring-primary/50 bg-primary/5'
										: ''}"
							>
								<div class="flex items-center gap-3">
									<div
										class="flex h-8 w-8 items-center justify-center rounded-full {getStatusColor(
											suggestion.status
										)}"
									>
										<StatusIcon class="h-4 w-4" />
									</div>
									<div>
										<p class="font-medium">
											{suggestion.nightOf ? formatNightOf(suggestion.nightOf) : 'Unknown date'}
										</p>
										<p class="text-sm text-muted-foreground">
											{time(suggestion.startMills ?? 0)} - {time(
												suggestion.endMills ?? 0
											)}
										</p>
									</div>
								</div>
								<Badge variant={getConfidenceVariant(suggestion.confidence ?? 0)} class="text-xs">
									{getConfidenceLabel(suggestion.confidence ?? 0)}
								</Badge>
							</div>
						</button>
					{/each}
				</div>

				<!-- Chart and Actions -->
				<div class="lg:col-span-2">
					{#if suggestionDetail}
						<Card>
							<CardHeader>
								<div class="flex items-center justify-between">
									<CardTitle>
										{suggestionDetail.suggestion?.nightOf
											? formatNightOf(suggestionDetail.suggestion.nightOf)
											: 'Unknown'}
									</CardTitle>
									<div
										class="flex h-8 w-8 items-center justify-center rounded-full {getStatusColor(
											suggestionDetail.suggestion?.status
										)}"
									>
										<DetailStatusIcon class="h-4 w-4" />
									</div>
								</div>
							</CardHeader>
							<CardContent>
								<!-- Glucose Chart with Brush -->
								{#if suggestionDetail?.entries && suggestionDetail.entries.length > 0 && chartDateRange}
									{#key chartDateRange.from.getTime() + '-' + chartDateRange.to.getTime()}
										{@const chartEngine = createChartDataEngine({
											dateRange: chartDateRange,
											enablePredictions: false,
										})}
										<div class="mb-6 h-64">
											<GlucoseChartShell
												engine={chartEngine}
												heightClass="h-64"
												selectionDomain={brushDomain}
												onSelectionChange={isPending ? handleSelectionChange : undefined}
											>
												{#snippet tracks(_ctx)}
													<ThresholdRules />
													<GlucoseTrack />
													<BasalTrack />
													<IobCobTrack />
												{/snippet}
												{#snippet overlays(_ctx)}
													<ChartTooltip />
												{/snippet}
											</GlucoseChartShell>
										</div>
									{/key}
								{/if}

								<!-- Stats -->
								<div class="mb-6 grid grid-cols-3 gap-4 text-center">
									<div>
										<p class="text-2xl font-bold">
											{suggestionDetail.suggestion?.lowestGlucose?.toFixed(0) ?? '-'}
										</p>
										<p class="text-sm text-muted-foreground">Lowest (mg/dL)</p>
									</div>
									<div>
										<p class="text-2xl font-bold">
											{suggestionDetail.suggestion?.dropRate?.toFixed(1) ?? '-'}
										</p>
										<p class="text-sm text-muted-foreground">Drop Rate (mg/dL/min)</p>
									</div>
									<div>
										<p class="text-2xl font-bold">{suggestionDetail.suggestion?.recoveryMinutes ?? '-'}</p>
										<p class="text-sm text-muted-foreground">Recovery (min)</p>
									</div>
								</div>

								<!-- Time Range Display -->
								{#if brushDomain}
									<div class="mb-6 rounded-lg bg-muted p-4">
										<p class="text-sm text-muted-foreground">
											{isPending ? 'Selected Range' : 'Exclusion Range'}
										</p>
										<p class="font-medium">
											{time(brushDomain[0])} - {time(brushDomain[1])}
										</p>
										{#if isPending}
											<p class="text-sm text-muted-foreground">
												Drag the handles on the chart to adjust
											</p>
										{/if}
									</div>
								{/if}

								<!-- Bulk Selection Bar -->
								{#if isBulkMode}
									<div class="mb-4 flex items-center justify-between rounded-lg bg-primary/10 p-3">
										<span class="text-sm font-medium">{selectionCount} selected</span>
										<div class="flex gap-2">
											<Button
												size="sm"
												onclick={handleAccept}
												disabled={isLoading}
											>
												<Check class="mr-1 h-3 w-3" />
												Accept All
											</Button>
											<Button
												size="sm"
												variant="outline"
												onclick={handleDismiss}
												disabled={isLoading}
											>
												<X class="mr-1 h-3 w-3" />
												Dismiss All
											</Button>
											<Button
												size="sm"
												variant="destructive"
												onclick={handleDelete}
												disabled={isLoading}
											>
												<Trash2 class="mr-1 h-3 w-3" />
												Delete All
											</Button>
										</div>
									</div>
								{/if}

								<!-- Actions -->
								{#if !isBulkMode}
									{#if isPending}
										<div class="flex gap-4">
											<Button
												class="flex-1"
												onclick={handleAccept}
												disabled={isLoading || !brushDomain}
											>
												<Check class="mr-2 h-4 w-4" />
												Accept
											</Button>
											<Button
												variant="outline"
												class="flex-1"
												onclick={handleDismiss}
												disabled={isLoading}
											>
												<X class="mr-2 h-4 w-4" />
												Dismiss
											</Button>
											<Button
												variant="destructive"
												class="flex-1"
												onclick={handleDelete}
												disabled={isLoading}
											>
												<Trash2 class="mr-2 h-4 w-4" />
												Delete
											</Button>
										</div>
									{:else}
										<div class="flex gap-4">
											<Button
												variant="destructive"
												class="flex-1"
												onclick={handleDelete}
												disabled={isLoading}
											>
												<Trash2 class="mr-2 h-4 w-4" />
												Delete
											</Button>
										</div>
									{/if}
								{/if}
							</CardContent>
						</Card>
					{:else}
						<Card>
							<CardContent class="py-12 text-center">
								<p class="text-muted-foreground">Select a compression low to view details</p>
							</CardContent>
						</Card>
					{/if}
				</div>
			</div>
		{/if}
	</div>
{/if}
