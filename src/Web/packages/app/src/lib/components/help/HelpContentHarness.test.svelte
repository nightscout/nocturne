<script lang="ts">
  import HelpReadingAid from "./HelpReadingAid.svelte";
  import { articleFor } from "$lib/help/documentation.svelte";
  let { path }: { path: string } = $props();
  const article = $derived(articleFor(path, []));
</script>

<HelpReadingAid {article} />
<h1>{article.title}</h1>
<p>{article.introduction}</p>
{#each article.sections as section (section.title)}<h2>{section.title}</h2>
  <p>{section.text}</p>{/each}
{#each article.steps ?? [] as step (step.title)}<h3>{step.title}</h3>
  <p>{step.text}</p>{/each}
