import { browser } from "$app/env";
import {
  PRESENTATIONS,
  setPresentation,
  type Presentation,
} from "water-ui";

const STORAGE_KEY = "nocturne-illustrations";

/** Per device: how well animation runs depends on the hardware, so this never syncs. */
function readStored(): Presentation {
  if (!browser) return "animated";
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return PRESENTATIONS.find((value) => value === stored) ?? "animated";
  } catch {
    return "animated";
  }
}

class Illustrations {
  #value = $state<Presentation>("animated");

  get current(): Presentation {
    return this.#value;
  }

  set current(value: Presentation) {
    this.#value = value;
    setPresentation(value);
    try {
      localStorage.setItem(STORAGE_KEY, value);
    } catch {
      // Storage can be blocked; the choice still holds for this session.
    }
  }

  /** Loads the stored choice into the library, before any artwork mounts. */
  init(): void {
    this.#value = readStored();
    setPresentation(this.#value);
  }
}

export const illustrations = new Illustrations();

/**
 * Whether watercolour the page could do without is worth loading or booting: not with
 * illustrations off, nor when the browser asks to save data. Reactive on the preference.
 */
export function watercolourWanted(): boolean {
  if (illustrations.current === "off") return false;
  if (typeof navigator === "undefined") return true;
  const connection: { saveData?: boolean } | undefined = Reflect.get(navigator, "connection");
  return !connection?.saveData;
}

/**
 * Whether deferred watercolour may load: wanted at all, and either past the first-interaction
 * window or a still, which draws its asset and boots no engine. Reactive on the preference.
 */
export function watercolourDue(windowPassed: boolean): boolean {
  if (!watercolourWanted()) return false;
  return windowPassed || illustrations.current === "still";
}
