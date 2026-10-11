import { getContext, onMount, setContext } from "svelte";
import type { NavItem } from "$lib/navigation/app-navigation.svelte";

const HELP = Symbol("nocturne-help");
const STORAGE_KEY = "nocturne-help-visibility";
export const helpModes = ["bar", "icon", "hidden"] as const;
export type HelpMode = (typeof helpModes)[number];

export function isHelpMode(value: unknown): value is HelpMode {
  return helpModes.some((mode) => mode === value);
}

export function createHelpContext() {
  let ready = $state(false);
  let mode = $state<HelpMode>("icon");
  let open = $state(false);
  let path = $state("/");
  let navigation = $state<NavItem[]>([]);

  onMount(() => {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (isHelpMode(saved)) mode = saved;
    } catch {
      /* Help remains usable when browser storage is unavailable. */
    }
    ready = true;
    const sync = (event: StorageEvent) => {
      if (event.key === STORAGE_KEY) {
        mode = isHelpMode(event.newValue) ? event.newValue : "icon";
        if (mode === "hidden") open = false;
      }
    };
    window.addEventListener("storage", sync);
    return () => window.removeEventListener("storage", sync);
  });

  const help = {
    get ready() {
      return ready;
    },
    get mode() {
      return mode;
    },
    get open() {
      return open;
    },
    set open(value: boolean) {
      open = value;
    },
    get path() {
      return path;
    },
    get navigation() {
      return navigation;
    },
    set navigation(value: NavItem[]) {
      navigation = value;
    },
    setMode(value: HelpMode) {
      mode = value;
      if (value === "hidden") open = false;
      try {
        localStorage.setItem(STORAGE_KEY, value);
      } catch {
        /* Session-only fallback. */
      }
    },
    show(value: string) {
      path = value;
      open = true;
    },
    toggle(value: string) {
      if (open) open = false;
      else {
        path = value;
        open = true;
      }
    },
    select(value: string) {
      path = value;
    },
  };
  setContext(HELP, help);
  return help;
}

export function getHelpContext() {
  return getContext<ReturnType<typeof createHelpContext> | undefined>(HELP);
}
