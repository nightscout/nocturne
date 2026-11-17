<script>
  // Counts as of 24 Oct 2026: contributors from the GitHub API (users, not bots), merged pull
  // requests from `git log --merges` on main, languages from src/Web/locales. Re-read them before a
  // re-render. No contributor is named: that needs their permission.
  import GitPullRequest from "@lucide/svelte/icons/git-pull-request";
  import Languages from "@lucide/svelte/icons/languages";
  import Users from "@lucide/svelte/icons/users";
  import MapPin from "@lucide/svelte/icons/map-pin";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";

  const stats = [
    { icon: Users, value: "31", label: "people have contributed code" },
    { icon: GitPullRequest, value: "678", label: "pull requests merged" },
    { icon: Languages, value: "10", label: "languages besides English" },
  ];
</script>

<div class="mx-auto max-w-5xl space-y-10 py-10">
  <div class="space-y-10" data-film="top">
  <div>
    <div class="text-7xl font-bold tracking-tight">Built by the community.</div>
    <div class="mt-4 text-3xl text-muted-foreground">Open source, for people who live with diabetes, by people who live with it.</div>
  </div>

  <div class="grid grid-cols-3 gap-6" data-film="stats">
    {#each stats as { icon: Icon, value, label } (label)}
      <div class="rounded-2xl border border-border bg-card p-8">
        <Icon class="size-8 text-muted-foreground" />
        <div class="mt-6 text-7xl font-bold tabular-nums">{value}</div>
        <div class="mt-2 text-xl text-muted-foreground">{label}</div>
      </div>
    {/each}
  </div>
  </div>

  <div class="rounded-2xl border border-border bg-card p-10" data-film="event">
    <div class="text-lg font-semibold uppercase tracking-widest text-muted-foreground">Come and build it with us</div>
    <div class="mt-3 text-6xl font-bold tracking-tight">HackDiabetes 26</div>
    <div class="mt-6 flex gap-10 text-2xl">
      <div class="flex items-center gap-3"><MapPin class="size-7 text-muted-foreground" /> Vancouver</div>
      <div class="flex items-center gap-3"><CalendarDays class="size-7 text-muted-foreground" /> 14 to 16 November</div>
    </div>
    <div class="mt-6 text-xl text-muted-foreground">Run by the Nightscout Foundation. All skill levels welcome. hackdiabetes.io</div>
  </div>
</div>
