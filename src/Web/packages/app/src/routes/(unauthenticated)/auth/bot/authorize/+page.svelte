<script lang="ts">
	import { page } from "$app/state";
	import { goto } from "$app/navigation";
	import { Loader2 } from "lucide-svelte";
	import { getBotAuthorizeContext, buildTenantRedirectUrl } from "../bot.remote";
	import { getPending, claimLink } from "$lib/api/generated/chatIdentities.generated.remote";

	// Read state token from URL
	const stateToken = $derived(page.url.searchParams.get("state") ?? "");

	// Get context (apex vs tenant)
	const contextQuery = getBotAuthorizeContext();
	const context = $derived(contextQuery.current);

	// On tenant subdomain, validate the pending token
	const pendingQuery = $derived(
		context?.mode === "tenant" && stateToken ? getPending(stateToken) : null,
	);
	const pending = $derived(pendingQuery?.current);
	const pendingError = $derived(pendingQuery?.error);

	// Determine mode
	const mode = $derived.by(() => {
		if (!stateToken) return "error" as const;
		if (!context) return "loading" as const;
		if (context.mode === "slug-prompt") return "slug-prompt" as const;
		if (pendingError) return "error" as const;
		if (!pending) return "loading" as const;
		return "confirm" as const;
	});

	const errorMessage = $derived.by(() => {
		if (!stateToken) return "Missing state token.";
		if (pendingError) return "This link is expired or invalid. Please run /connect in your chat app again.";
		return "";
	});

	// Slug prompt form state
	let slug = $state("");
	let slugError = $state<string | null>(null);
	let isSubmittingSlug = $state(false);

	// Claim form state
	let isClaiming = $state(false);
	let claimError = $state<string | null>(null);

	const returnUrl = $derived(
		mode === "confirm" ? `/auth/bot/authorize?state=${stateToken}` : "/",
	);

	async function handlePickTenant() {
		isSubmittingSlug = true;
		slugError = null;
		try {
			const result = await buildTenantRedirectUrl({ slug, stateToken });
			if ("error" in result && result.error) {
				slugError = result.error;
			} else if ("url" in result && result.url) {
				window.location.href = result.url;
				return; // Don't reset loading state - we're navigating away
			}
		} catch {
			slugError = "Failed to build redirect URL.";
		}
		isSubmittingSlug = false;
	}

	async function handleClaim() {
		isClaiming = true;
		claimError = null;
		try {
			await claimLink({ token: stateToken });
			goto("/auth/bot/authorize/done");
		} catch {
			claimError = "Failed to link account. Please try again.";
			isClaiming = false;
		}
	}
</script>

{#if mode === "loading"}
	<div class="flex items-center justify-center min-h-screen">
		<Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
	</div>
{:else if mode === "error"}
	<div class="flex flex-col items-center justify-center min-h-screen gap-4 p-6 text-center">
		<h1 class="text-2xl font-bold">Can't Complete This Link</h1>
		<p class="text-destructive max-w-md">{errorMessage}</p>
	</div>
{:else if mode === "slug-prompt"}
	<div class="flex flex-col items-center justify-center min-h-screen gap-4 p-6">
		<h1 class="text-2xl font-bold">Which Nocturne Instance?</h1>
		<p class="text-muted-foreground max-w-md text-center">
			Enter the slug of the Nocturne instance you'd like to link your chat account to.
		</p>
		{#if slugError}
			<p class="text-destructive">{slugError}</p>
		{/if}
		<form
			onsubmit={(e) => { e.preventDefault(); handlePickTenant(); }}
			class="flex flex-col gap-3 w-full max-w-sm"
		>
			<label class="flex flex-col gap-1">
				<span class="text-sm font-medium">Instance slug</span>
				<input
					type="text"
					bind:value={slug}
					required
					pattern="[a-z0-9][a-z0-9\-]{'{ 0,62}'}[a-z0-9]?"
					placeholder="e.g. myfamily"
					class="px-3 py-2 border rounded-md bg-background"
				/>
			</label>
			<button
				type="submit"
				disabled={isSubmittingSlug}
				class="px-6 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50"
			>
				{#if isSubmittingSlug}
					<Loader2 class="inline h-4 w-4 animate-spin mr-1" />
				{/if}
				Continue
			</button>
		</form>
	</div>
{:else if mode === "confirm"}
	<div class="flex flex-col items-center justify-center min-h-screen gap-4 p-6">
		<h1 class="text-2xl font-bold">Connect Chat Account</h1>
		{#if !context?.isAuthenticated}
			<p class="text-muted-foreground max-w-md text-center">
				Sign in to your Nocturne account to finish connecting your chat account.
			</p>
			<a
				href="/auth/login?returnUrl={encodeURIComponent(returnUrl)}"
				class="px-6 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
			>
				Sign in
			</a>
		{:else}
			<p class="text-muted-foreground max-w-md text-center">
				Allow the Nocturne bot to access glucose data on your behalf and send alerts to your
				<strong>{pending?.platform}</strong> account?
			</p>
			{#if claimError}
				<p class="text-destructive">{claimError}</p>
			{/if}
			<button
				type="button"
				onclick={handleClaim}
				disabled={isClaiming}
				class="px-6 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50"
			>
				{#if isClaiming}
					<Loader2 class="inline h-4 w-4 animate-spin mr-1" />
				{/if}
				Connect
			</button>
		{/if}
	</div>
{/if}
