<!--
  A server render in another display language: the language is published through the request
  context, as the app's root layout does. Shows a component, or the text a function reads while
  the render runs.
-->
<script lang="ts" generics="P extends object">
  import type { Component } from "svelte";
  import { setPreferencesContext, type SupportedLocale } from "#lib/stores/appearance-store.svelte.js";

  interface Props {
    language: SupportedLocale;
    component?: Component<P>;
    viewProps?: P;
    read?: () => string;
  }

  let { language, component: View, viewProps, read }: Props = $props();

  setPreferencesContext(() => ({ layers: [], language }));
</script>

{#if read}{read()}{:else if View && viewProps}<View {...viewProps} />{/if}
