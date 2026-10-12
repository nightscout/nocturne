<script lang="ts">
  import { createHelpContext } from "$lib/help/help-context.svelte";
  import { buildAppNavigation } from "$lib/navigation/app-navigation.svelte";
  import HelpBar from "./HelpBar.svelte";
  import HelpPageView from "./HelpPageView.svelte";
  import HelpIcon from "./HelpIcon.svelte";
  import HelpPreferences from "./HelpPreferences.svelte";
  import { Input } from "$lib/components/ui/input";
  let {
    hiddenMobileHeader = false,
    tallPage = false,
    applicationDialog,
  }: {
    hiddenMobileHeader?: boolean;
    tallPage?: boolean;
    applicationDialog?: "dialog" | "alertdialog";
  } = $props();
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
  {#if hiddenMobileHeader}<div
      data-slot="sidebar-header"
      class="offscreen-header"
    ></div>{/if}
  <header
    data-help-header
    class:sticky={tallPage}
    class:top-0={tallPage}
    class:z-40={tallPage}
    class:hidden-header={hiddenMobileHeader && !help.open}
    class="flex items-center gap-2 border-b p-4 font-bold"
  >
    Nocturne <HelpIcon />
  </header>
  <HelpBar />
  <main>
    <HelpPageView>
      <div class="mx-auto max-w-4xl space-y-6 p-6" class:tall-page={tallPage}>
        <h1 class="text-2xl font-bold">Steps</h1>
        <p>Your activity report stays open while you explore documentation.</p>
        <Input aria-label="Report notes" />
        <HelpPreferences />
      </div>
    </HelpPageView>
  </main>
</div>

{#if applicationDialog}
  <div
    role={applicationDialog}
    aria-modal="true"
    aria-label="Application dialog"
    tabindex="-1"
    class="fixed right-4 top-24 z-[100] w-64 rounded-xl border bg-background p-4"
  >
    <!-- svelte-ignore a11y_no_noninteractive_tabindex (The named test scroll region exercises native keyboard scrolling.) -->
    <div
      role="region"
      aria-label="Application dialog content"
      tabindex="0"
      class="h-40 overflow-y-auto overscroll-contain"
    >
      <p class="dialog-test-content">Dialog content</p>
    </div>
  </div>
{/if}

<style>
  .dialog-test-content {
    height: 800px;
  }
  .tall-page {
    min-height: 300vh;
  }
  .offscreen-header {
    position: fixed;
    left: -1000px;
    width: 288px;
    height: 56px;
  }
  .hidden-header {
    transform: translateY(-100%);
  }
</style>
