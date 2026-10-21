<script lang="ts">
	import { page } from "$app/state";
	import { goto } from "$app/navigation";
	import { resolve } from "$app/paths";
	import Loader2 from "@lucide/svelte/icons/loader-circle";
	import { Button } from "$lib/components/ui/button";
	import { retainQuery } from "$lib/api/retain-query.svelte";
	import { describeSubmitError, errorStatus } from "$lib/forms/submit-error";
	import { confirmDiscordLink, getDiscordLinkClaim } from "./finalize.remote";

	const claim = $derived(page.url.searchParams.get("claim") ?? "");

	$effect(() => {
		if (!page.data.isAuthenticated && claim) {
			const returnUrl = `/auth/bot/discord/finalize?claim=${encodeURIComponent(claim)}`;
			goto(resolve(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`), { replaceState: true });
		}
	});

	const claimQuery = $derived(page.data.isAuthenticated && claim ? getDiscordLinkClaim(claim) : null);
	retainQuery(() => claimQuery);
	const pending = $derived(claimQuery?.current);

	let linked = $state<{ label: string; displayName: string } | null>(null);
	let isConfirming = $state(false);
	let confirmError = $state<string | null>(null);

	async function handleConfirm() {
		isConfirming = true;
		confirmError = null;
		try {
			linked = await confirmDiscordLink(claim);
		} catch (err: unknown) {
			const status = errorStatus(err);
			confirmError =
				status === 409
					? "This Discord account is already linked to another member."
					: status === 400
						? "This link is expired or invalid. Please try again from Settings."
						: describeSubmitError(
								err,
								"We couldn't complete the Discord link. It may have expired. Please try again from Settings.",
							);
		} finally {
			isConfirming = false;
		}
	}
</script>

<div class="flex flex-col items-center justify-center min-h-screen gap-4 p-6 text-center">
	{#if !claim}
		<h1 class="text-2xl font-bold">Missing link details</h1>
		<p class="text-destructive max-w-md">Start linking your Discord account again from Settings.</p>
	{:else if linked}
		<h1 class="text-2xl font-bold">Discord account linked</h1>
		<p class="text-muted-foreground max-w-md">
			Your Discord account is now linked as <strong>{linked.displayName}</strong>
			(label <code>{linked.label}</code>). Run <code>/bg</code> in Discord to get started.
		</p>
		<a
			href={resolve("/settings/integrations/discord")}
			class="px-6 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
		>
			Back to Settings
		</a>
	{:else if pending === null || claimQuery?.error}
		<h1 class="text-2xl font-bold">Can't complete this link</h1>
		<p class="text-destructive max-w-md">
			This link has expired, or was started from another browser or account. Start linking your
			Discord account again from Settings.
		</p>
		<a
			href={resolve("/settings/integrations/discord")}
			class="px-6 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
		>
			Back to Settings
		</a>
	{:else if pending}
		<h1 class="text-2xl font-bold">Link Discord account</h1>
		<p class="text-muted-foreground max-w-md">
			{#if pending.discordUsername}
				Link the Discord account <strong>@{pending.discordUsername}</strong> to your Nocturne account?
			{:else}
				Link the Discord account you just signed in with to your Nocturne account?
			{/if}
			It will be able to read your glucose and acknowledge your alerts.
		</p>
		{#if confirmError}
			<p class="text-destructive max-w-md">{confirmError}</p>
		{/if}
		<Button onclick={handleConfirm} disabled={isConfirming}>
			{#if isConfirming}
				<Loader2 class="inline h-4 w-4 animate-spin mr-1" />
			{/if}
			Link account
		</Button>
	{:else}
		<Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
	{/if}
</div>
