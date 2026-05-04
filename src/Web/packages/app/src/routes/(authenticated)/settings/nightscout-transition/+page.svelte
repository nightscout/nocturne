<script lang="ts">
	import {
		Card,
		CardContent,
		CardDescription,
		CardHeader,
		CardTitle,
	} from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import { Button } from '$lib/components/ui/button';
	import { Separator } from '$lib/components/ui/separator';
	import {
		ArrowRightLeft,
		Activity,
		AlertCircle,
		CheckCircle2,
		Loader2,
		Database,
		ShieldCheck,
		ShieldAlert,
		Clock,
		BarChart3,
	} from 'lucide-svelte';
	import { goto } from '$app/navigation';
	import {
		getTransitionStatus,
		type NightscoutTransitionStatus,
	} from './transition.remote';

	let loading = $state(true);
	let error = $state<string | null>(null);
	let status = $state<NightscoutTransitionStatus | null>(null);

	async function loadData() {
		loading = true;
		error = null;
		try {
			status = await getTransitionStatus();
		} catch (err) {
			console.error('Failed to load transition status:', err);
			error = 'Failed to load Nightscout transition status';
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		loadData();
	});

	function formatTimestamp(ts: string | null): string {
		if (!ts) return 'Never';
		try {
			return new Date(ts).toLocaleString();
		} catch {
			return 'Unknown';
		}
	}

	const recommendationColor = $derived.by(() => {
		if (!status) return 'destructive' as const;
		switch (status.recommendation.status) {
			case 'safe':
				return 'default' as const;
			case 'almost-ready':
				return 'secondary' as const;
			case 'not-ready':
			default:
				return 'destructive' as const;
		}
	});

	const recommendationLabel = $derived.by(() => {
		if (!status) return 'Unknown';
		switch (status.recommendation.status) {
			case 'safe':
				return 'Safe to Disconnect';
			case 'almost-ready':
				return 'Almost Ready';
			case 'not-ready':
			default:
				return 'Not Ready';
		}
	});

	const compatibilityBadgeVariant = $derived.by(() => {
		const score = status?.compatibility?.compatibilityScore;
		if (score == null) return 'secondary' as const;
		if (score >= 95) return 'default' as const;
		if (score >= 80) return 'secondary' as const;
		return 'destructive' as const;
	});

	const compatibilityBadgeLabel = $derived.by(() => {
		const score = status?.compatibility?.compatibilityScore;
		if (score == null) return 'No Data';
		if (score >= 95) return 'High';
		if (score >= 80) return 'Medium';
		return 'Low';
	});
</script>

<svelte:head>
	<title>Nightscout Transition - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
	<!-- Header -->
	<div class="flex items-center gap-3">
		<div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
			<ArrowRightLeft class="h-6 w-6 text-primary" />
		</div>
		<div>
			<h1 class="text-2xl font-bold tracking-tight">Nightscout Transition</h1>
			<p class="text-muted-foreground">
				Monitor your migration from Nightscout and check disconnect readiness
			</p>
		</div>
	</div>

	{#if loading}
		<div class="flex items-center justify-center py-16">
			<Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
		</div>
	{:else if error}
		<Card class="border-destructive">
			<CardContent class="flex items-center gap-3 py-6">
				<AlertCircle class="h-5 w-5 text-destructive" />
				<div>
					<p class="font-medium">Failed to load transition status</p>
					<p class="text-sm text-muted-foreground">{error}</p>
				</div>
			</CardContent>
		</Card>
	{:else if status}
		<!-- Migration Status -->
		<Card>
			<CardHeader>
				<div class="flex items-center justify-between">
					<CardTitle class="flex items-center gap-2">
						<Database class="h-5 w-5" />
						Migration Status
					</CardTitle>
					<Badge variant={status.migration.isComplete ? 'default' : 'secondary'}>
						{#if status.migration.isComplete}
							<CheckCircle2 class="h-3 w-3" />
							Complete
						{:else}
							<Loader2 class="h-3 w-3 animate-spin" />
							In Progress
						{/if}
					</Badge>
				</div>
				<CardDescription>
					Record counts by data type from your Nightscout instance
				</CardDescription>
			</CardHeader>
			<CardContent class="space-y-4">
				{#if Object.keys(status.migration.recordCounts).length > 0}
					<div class="grid grid-cols-2 gap-3 sm:grid-cols-3">
						{#each Object.entries(status.migration.recordCounts) as [dataType, count]}
							<div class="rounded-lg border p-3">
								<p class="text-sm text-muted-foreground capitalize">{dataType}</p>
								<p class="text-xl font-semibold tabular-nums">{count.toLocaleString()}</p>
							</div>
						{/each}
					</div>
				{:else}
					<p class="text-sm text-muted-foreground">No migration data available yet.</p>
				{/if}

				<Separator />

				<div class="flex items-center gap-2 text-sm text-muted-foreground">
					<Clock class="h-4 w-4" />
					<span>Last sync: {formatTimestamp(status.migration.lastSyncTime)}</span>
				</div>
			</CardContent>
		</Card>

		<!-- Write-Back Health -->
		<Card>
			<CardHeader>
				<div class="flex items-center justify-between">
					<CardTitle class="flex items-center gap-2">
						<Activity class="h-5 w-5" />
						Write-Back Health (Last 24 Hours)
					</CardTitle>
					<Badge variant={status.writeBack.circuitBreakerOpen ? 'destructive' : 'default'}>
						{#if status.writeBack.circuitBreakerOpen}
							<ShieldAlert class="h-3 w-3" />
							Circuit Open
						{:else}
							<ShieldCheck class="h-3 w-3" />
							Healthy
						{/if}
					</Badge>
				</div>
				<CardDescription>
					Status of write-back operations to your Nightscout instance
				</CardDescription>
			</CardHeader>
			<CardContent class="space-y-4">
				<div class="grid grid-cols-3 gap-3">
					<div class="rounded-lg border p-3">
						<p class="text-sm text-muted-foreground">Requests</p>
						<p class="text-xl font-semibold tabular-nums">
							{status.writeBack.requestsLast24h.toLocaleString()}
						</p>
					</div>
					<div class="rounded-lg border p-3">
						<p class="text-sm text-muted-foreground">Succeeded</p>
						<p class="text-xl font-semibold tabular-nums text-green-600 dark:text-green-400">
							{status.writeBack.successesLast24h.toLocaleString()}
						</p>
					</div>
					<div class="rounded-lg border p-3">
						<p class="text-sm text-muted-foreground">Failed</p>
						<p class="text-xl font-semibold tabular-nums text-red-600 dark:text-red-400">
							{status.writeBack.failuresLast24h.toLocaleString()}
						</p>
					</div>
				</div>

				<Separator />

				<div class="flex items-center gap-2 text-sm text-muted-foreground">
					<Clock class="h-4 w-4" />
					<span>
						Last successful write-back: {formatTimestamp(status.writeBack.lastSuccessTime)}
					</span>
				</div>
			</CardContent>
		</Card>

		<!-- API Compatibility -->
		{#if status.compatibility}
			<Card>
				<CardHeader>
					<div class="flex items-center justify-between">
						<CardTitle class="flex items-center gap-2">
							<ShieldCheck class="h-5 w-5" />
							API Compatibility
						</CardTitle>
						<Badge variant={compatibilityBadgeVariant}>
							{#if compatibilityBadgeVariant === 'default'}
								<CheckCircle2 class="h-3 w-3" />
							{:else if compatibilityBadgeVariant === 'destructive'}
								<AlertCircle class="h-3 w-3" />
							{:else}
								<BarChart3 class="h-3 w-3" />
							{/if}
							{compatibilityBadgeLabel}
						</Badge>
					</div>
					<CardDescription>
						Background comparison of Nocturne responses against your Nightscout instance
					</CardDescription>
				</CardHeader>
				<CardContent class="space-y-4">
					<div class="grid grid-cols-3 gap-3">
						<div class="rounded-lg border p-3">
							<p class="text-sm text-muted-foreground">Score</p>
							<p class="text-2xl font-semibold tabular-nums">
								{status.compatibility.compatibilityScore != null
									? `${status.compatibility.compatibilityScore.toFixed(1)}%`
									: '--'}
							</p>
						</div>
						<div class="rounded-lg border p-3">
							<p class="text-sm text-muted-foreground">Comparisons</p>
							<p class="text-xl font-semibold tabular-nums">
								{status.compatibility.totalComparisons.toLocaleString()}
							</p>
						</div>
						<div class="rounded-lg border p-3">
							<p class="text-sm text-muted-foreground">Discrepancies</p>
							<p class="text-xl font-semibold tabular-nums text-red-600 dark:text-red-400">
								{status.compatibility.discrepancies.toLocaleString()}
							</p>
						</div>
					</div>
				</CardContent>
			</Card>
		{/if}

		<!-- Safe to Disconnect -->
		<Card>
			<CardHeader>
				<CardTitle class="flex items-center gap-2">
					<ArrowRightLeft class="h-5 w-5" />
					Safe to Disconnect
				</CardTitle>
				<CardDescription>
					Recommendation on whether it is safe to stop using your Nightscout instance
				</CardDescription>
			</CardHeader>
			<CardContent class="space-y-4">
				<div class="flex items-center gap-3">
					<Badge variant={recommendationColor} class="text-sm px-3 py-1">
						{#if status.recommendation.status === 'safe'}
							<CheckCircle2 class="h-4 w-4" />
						{:else if status.recommendation.status === 'almost-ready'}
							<Clock class="h-4 w-4" />
						{:else}
							<AlertCircle class="h-4 w-4" />
						{/if}
						{recommendationLabel}
					</Badge>

					{#if status.recommendation.status === 'almost-ready' && status.recommendation.stabilityDaysRemaining != null}
						<span class="text-sm text-muted-foreground">
							{status.recommendation.stabilityDaysRemaining} stability
							{status.recommendation.stabilityDaysRemaining === 1 ? 'day' : 'days'} remaining
						</span>
					{/if}
				</div>

				{#if status.recommendation.blockers.length > 0}
					<Separator />
					<div class="space-y-2">
						<p class="text-sm font-medium">Blockers</p>
						<ul class="space-y-1.5">
							{#each status.recommendation.blockers as blocker}
								<li class="flex items-start gap-2 text-sm text-muted-foreground">
									<AlertCircle class="h-4 w-4 mt-0.5 shrink-0 text-destructive" />
									{blocker}
								</li>
							{/each}
						</ul>
					</div>
				{/if}

				{#if status.recommendation.status === 'safe'}
					<Separator />
					<div>
						<Button variant="outline" onclick={() => goto('/settings/connectors')}>
							Go to Connector Settings
							<ArrowRightLeft class="h-4 w-4 ml-2" />
						</Button>
					</div>
				{/if}
			</CardContent>
		</Card>
	{/if}
</div>
