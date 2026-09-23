<script lang="ts">
	import { page } from "$app/state";
	import { goto } from "$app/navigation";
	import { resolve } from "$app/paths";
	import { Loader2 } from "lucide-svelte";
	import { claimLink } from "$lib/api/generated/chatIdentities.generated.remote";

	// Read token from URL
	const token = $derived(page.url.searchParams.get("token") ?? "");

	// Auth guard: redirect to login if not authenticated
	$effect(() => {
		if (!page.data.isAuthenticated && token) {
			const returnUrl = `/auth/bot/discord/finalize?token=${token}`;
			goto(resolve(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`), { replaceState: true });
		}
	});

	// Claim state
	let result = $state<{ success: true; label: string; displayName: string } | { success: false; message: string } | null>(null);
	let loading = $state(true);

	// Auto-claim on mount if authenticated
	$effect(() => {
		if (page.data.isAuthenticated && token && !result) {
			claimToken();
		}
	});

	async function claimToken() {
		loading = true;
		try {
			const link = await claimLink({ token });
			result = {
				success: true,
				label: (link as any).label ?? "",
				displayName: (link as any).displayName ?? "",
			};
		} catch (err: unknown) {
			console.error("Failed to claim OAuth2 link:", err);
			result = {
				success: false,
				message: "We couldn't complete the Discord link. The token may have expired. Please try again from Settings.",
			};
		} finally {
			loading = false;
		}
	}
</script>

<div class="flex flex-col items-center justify-center min-h-screen gap-4 p-6 text-center">
	{#if !token}
		<h1 class="text-2xl font-bold">Missing Token</h1>
		<p class="text-destructive max-w-md">Missing token parameter.</p>
	{:else if loading}
		<Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
	{:else if result?.success}
		<h1 class="text-2xl font-bold">Discord account linked</h1>
		<p class="text-muted-foreground max-w-md">
			Your Discord account is now linked as <strong>{result.displayName}</strong>
			(label <code>{result.label}</code>). Run <code>/bg</code> in Discord to get started.
		</p>
		<a
			href={resolve("/settings/integrations/discord")}
			class="px-6 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
		>
			Back to Settings
		</a>
	{:else if result && !result.success}
		<h1 class="text-2xl font-bold">Link failed</h1>
		<p class="text-destructive max-w-md">{result.message}</p>
		<a
			href={resolve("/settings/integrations/discord")}
			class="px-6 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
		>
			Back to Settings
		</a>
	{/if}
</div>
