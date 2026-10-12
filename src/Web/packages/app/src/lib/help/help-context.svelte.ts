import { getContext, onMount, setContext } from "svelte";
import type { NavItem } from "$lib/navigation/app-navigation.svelte";

const HELP = Symbol("nocturne-help");
const STORAGE_KEY = "nocturne-help-visibility";
const LAYOUT_KEY = "nocturne-help-layout";
export const helpLayouts = ["drawer", "right", "alongside", "page"] as const;
export type HelpLayout = (typeof helpLayouts)[number];
export function isHelpLayout(value: unknown): value is HelpLayout {
  return helpLayouts.some((layout) => layout === value);
}
export function helpLayoutLabel(layout: HelpLayout): string {
  const labels: Record<HelpLayout, string> = {
    drawer: "Navigation drawer",
    right: "Right drawer",
    alongside: "Side by side",
    page: "Replace page",
  };
  return labels[layout];
}
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
  let layout = $state<HelpLayout>("drawer");
  let top = $state(64);
  let seam = $state(0);
  let compact = $state(false);

  function measureHeaderTop() {
    const headers = document.querySelectorAll<HTMLElement>(
      '[data-slot="sidebar-header"], [data-help-header]'
    );
    const header = Array.from(headers).find((element) => {
      const rect = element.getBoundingClientRect();
      return rect.width > 0 && rect.right > 0 && rect.left < window.innerWidth;
    });
    const rect = header?.getBoundingClientRect();
    seam = header?.hasAttribute("data-help-header")
      ? 0
      : Math.max(0, rect?.right ?? 0);
    return header?.hasAttribute("data-help-header")
      ? Math.max(rect?.height ?? 56, rect?.bottom ?? 56)
      : Math.max(0, rect?.bottom ?? 64);
  }

  onMount(() => {
    const media = window.matchMedia("(max-width: 1100px)");
    const syncSize = () => {
      compact = media.matches;
    };
    syncSize();
    media.addEventListener("change", syncSize);
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (isHelpMode(saved)) mode = saved;
      const savedLayout = localStorage.getItem(LAYOUT_KEY);
      if (isHelpLayout(savedLayout)) layout = savedLayout;
    } catch {
      /* Help remains usable when browser storage is unavailable. */
    }
    ready = true;
    const sync = (event: StorageEvent) => {
      if (event.key === STORAGE_KEY) {
        mode = isHelpMode(event.newValue) ? event.newValue : "icon";
        if (mode === "hidden") open = false;
      }
      if (event.key === LAYOUT_KEY) {
        layout = isHelpLayout(event.newValue) ? event.newValue : "drawer";
      }
    };
    window.addEventListener("storage", sync);
    return () => {
      window.removeEventListener("storage", sync);
      media.removeEventListener("change", syncSize);
    };
  });

  const help = {
    get blocking() {
      return open && layout !== "page" && (layout !== "alongside" || compact);
    },
    get layout() {
      return layout;
    },
    get top() {
      return top;
    },
    get seam() {
      return seam;
    },
    setLayout(value: HelpLayout) {
      layout = value;
      try {
        localStorage.setItem(LAYOUT_KEY, value);
      } catch {
        /* Session-only fallback. */
      }
    },
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
      top = measureHeaderTop();
      path = value;
      open = true;
    },
    toggle(value: string) {
      if (open) open = false;
      else {
        top = measureHeaderTop();
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
