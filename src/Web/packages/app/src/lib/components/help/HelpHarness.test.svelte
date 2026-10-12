<script lang="ts">
  import { createHelpContext } from "$lib/help/help-context.svelte";
  import { buildAppNavigation } from "$lib/navigation/app-navigation.svelte";
  import HelpBar from "./HelpBar.svelte";
  import HelpPageView from "./HelpPageView.svelte";
  import HelpIcon from "./HelpIcon.svelte";
  import HelpPreferences from "./HelpPreferences.svelte";
  import { Input } from "$lib/components/ui/input";
  const help = createHelpContext();
  help.navigation = buildAppNavigation({
    user: {},
    isGuestSession: false,
    isPlatformAdmin: false,
    grantedScopes: ["*"],
    tenantCount: 1,
    tenantless: false,
  });
</script>

<div class="min-h-screen bg-background text-foreground">
  <header class="flex items-center gap-2 border-b p-4 font-bold">
    Nocturne <HelpIcon />
  </header>
  <HelpBar />
  <main>
    <HelpPageView>
      <div class="mx-auto max-w-4xl space-y-6 p-6">
        <h1 class="text-2xl font-bold">Steps</h1>
        <p>Your activity report stays open while you explore documentation.</p>
        <Input aria-label="Report notes" />
        <HelpPreferences />
      </div>
    </HelpPageView>
  </main>
</div>
