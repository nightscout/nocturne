import type { CoachMarkOptions, MarkRegistration } from "./types.js";
import type { CoachMarkContext } from "./context.svelte.js";

// Module-level context reference, set by the provider
let _ctx: CoachMarkContext | null = null;

export function setCoachMarkContextRef(ctx: CoachMarkContext): void {
  _ctx = ctx;
}

function buildRegistration(
  options: CoachMarkOptions,
  element: HTMLElement,
): MarkRegistration | null {
  const steps =
    options.steps ??
    (options.title
      ? [{ title: options.title, description: options.description ?? "" }]
      : []);

  if (steps.length === 0) {
    console.warn(`[coach] Mark "${options.key}" has no content.`);
    return null;
  }

  const stepIndex = options.step ?? 0;
  const stepContent = steps[stepIndex] ?? steps[0];

  return {
    key: options.key,
    step: stepIndex,
    title: stepContent.title,
    description: stepContent.description,
    action: options.action,
    completedWhen: options.completedWhen,
    completeOn: options.completeOn,
    priority: options.priority ?? 0,
    element,
  };
}

export function coachmark(options: CoachMarkOptions | CoachMarkOptions[]) {
  return (element: HTMLElement) => {
    const ctx = _ctx;
    if (!ctx) {
      const label = Array.isArray(options)
        ? options.map((o) => o.key).join(", ")
        : options.key;
      console.warn("[coach] No CoachMarkProvider found. Mark ignored:", label);
      return;
    }

    const optionsArray = Array.isArray(options) ? options : [options];

    // Build registrations and register all keys
    const registrations: { opts: CoachMarkOptions; reg: MarkRegistration; unregister: () => void }[] = [];

    for (const opts of optionsArray) {
      const reg = buildRegistration(opts, element);
      if (!reg) continue;
      const unregister = ctx.register(reg);
      registrations.push({ opts, reg, unregister });
    }

    if (registrations.length === 0) return;

    // Create ONE hotspot dot regardless of how many keys
    const dot = document.createElement("button");
    dot.className = "coach-hotspot";
    dot.setAttribute("aria-label", "Show tip");
    dot.setAttribute("type", "button");
    dot.addEventListener("click", (e) => {
      e.stopPropagation();
      // Delegate to whichever key is currently active/eligible
      for (const { opts, reg } of registrations) {
        const status = ctx.getStatus(opts.key);
        if (status === "completed" || status === "dismissed") continue;
        if (!ctx.isMarkEligible(opts.key)) continue;
        ctx.activate(opts.key, reg.step);
        return;
      }
      // Fallback: activate first key
      ctx.activate(registrations[0].opts.key, registrations[0].reg.step);
    });

    // Position the element relatively if needed
    const computedPosition = getComputedStyle(element).position;
    if (computedPosition === "static") {
      element.style.position = "relative";
    }
    element.appendChild(dot);

    // completeOn event listener management (per key)
    const completeOnCleanups: (() => void)[] = [];
    const completeOnAttached = new Set<string>();

    function attachCompleteOnListener(opts: CoachMarkOptions): void {
      if (completeOnAttached.has(opts.key) || !opts.completeOn) return;

      const { event, target } = opts.completeOn;
      let targetEl: HTMLElement | null = null;

      if (!target) {
        targetEl = element;
      } else if (typeof target === "string") {
        targetEl = element.querySelector(target);
      } else {
        targetEl = target;
      }

      if (!targetEl) return; // target not available yet, retry on next poll

      const handler = () => {
        ctx!.complete(opts.key);
      };

      targetEl.addEventListener(event, handler, { once: true });
      completeOnAttached.add(opts.key);
      completeOnCleanups.push(() => targetEl!.removeEventListener(event, handler));
    }

    // Visibility update interval
    const updateVisibility = () => {
      let anyVisible = false;
      let anyUnseen = false;

      for (const { opts } of registrations) {
        const status = ctx.getStatus(opts.key);
        const eligible = ctx.isMarkEligible(opts.key);

        if (status !== "completed" && status !== "dismissed" && eligible) {
          anyVisible = true;
          if (status === "unseen") {
            anyUnseen = true;
          }
        }

        // Check completedWhen callback for each key
        if (opts.completedWhen && status !== "completed" && status !== "dismissed") {
          if (opts.completedWhen()) {
            ctx.complete(opts.key);
          }
        }

        // Lazily attach completeOn listener for each key
        if (opts.completeOn && !completeOnAttached.has(opts.key) && status !== "completed" && status !== "dismissed") {
          attachCompleteOnListener(opts);
        }
      }

      if (!anyVisible) {
        dot.style.display = "none";
      } else if (anyUnseen) {
        dot.classList.add("coach-hotspot--pulse");
        dot.style.display = "";
      } else {
        dot.classList.remove("coach-hotspot--pulse");
        dot.style.display = "";
      }
    };

    updateVisibility();
    const interval = setInterval(updateVisibility, 500);

    // Cleanup
    return () => {
      clearInterval(interval);
      for (const cleanup of completeOnCleanups) cleanup();
      for (const { unregister } of registrations) unregister();
      dot.remove();
    };
  };
}
