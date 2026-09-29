import { browser } from "$app/environment";
import {
  PRESENTATIONS,
  setPresentation,
  type Presentation,
} from "@nocturne/watercolour";

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
