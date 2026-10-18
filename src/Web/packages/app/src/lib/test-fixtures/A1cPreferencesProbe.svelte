<script lang="ts">
  import {
    setPreferencesContext,
    type RequestPreferences,
  } from "../stores/appearance-store.svelte";
  import { a1cLabel, formatA1c } from "../utils/a1c-formatting";
  import { reportCategories } from "../navigation/report-navigation.svelte";

  let { layers }: RequestPreferences = $props();
  setPreferencesContext(() => ({ layers }));
  const report = reportCategories()
    .flatMap((category) => category.reports)
    .find((item) => item.href === "/reports/ehba1c");
</script>

<span>
  {a1cLabel()} / {a1cLabel(true)}: {formatA1c({ percent: 7, mmolMol: 53 })}
</span>
<nav>{report?.sidebarTitle}</nav>
<h1>{report?.title}</h1>
<p>{report?.description}</p>
