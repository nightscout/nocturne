/**
 * Writes a film's HyperFrames project, `compositions/<name>/`: the page that mounts the bundle and
 * registers the film's timeline, with its length on the root (which HyperFrames reads before any
 * script runs), and the project's config. Every film's project is this one page; only the logo's
 * is written by hand.
 */
import { mkdirSync, writeFileSync } from "node:fs";
import { schedule } from "../src/tour/timing.js";

const escape = (text) => text.replaceAll("&", "&amp;").replaceAll("<", "&lt;");

/** `source` is the film's path, named in the page so a reader of the build knows where to edit. */
export function writeComposition(name, film, source) {
  const dir = new URL(`../compositions/${name}/`, import.meta.url);
  mkdirSync(dir, { recursive: true });
  const { total } = schedule(film);
  writeFileSync(
    new URL("index.html", dir),
    `<!doctype html>
<html lang="en" class="dark">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=1920, height=1080" />
    <title>Nocturne ${escape(film.intro?.title ?? name)}</title>
    <script src="https://cdn.jsdelivr.net/npm/gsap@3.14.2/dist/gsap.min.js"></script>
    <link rel="stylesheet" href="vendor/report.css" />
    <script src="vendor/report.js"></script>
  </head>
  <body>
    <!-- Written by the build from ${source}. -->
    <div id="root" data-composition-id="main" data-start="0" data-width="1920" data-height="1080" data-duration="${total}"></div>
    <script>
      (async () => {
        await document.fonts.ready;
        window.__timelines["main"] = (await NocturneTour.film(document.getElementById("root"))).timeline;
        // The film waits for the page to settle, so the timeline arrives after load.
        window.__hfForceTimelineRebind?.();
      })();
    </script>
  </body>
</html>
`
  );
  writeFileSync(
    new URL("hyperframes.json", dir),
    JSON.stringify(
      {
        $schema: "https://hyperframes.heygen.com/schema/hyperframes.json",
        registry: "https://raw.githubusercontent.com/heygen-com/hyperframes/main/registry",
        paths: { blocks: "compositions", components: "compositions/components", assets: "assets" },
      },
      null,
      2
    ) + "\n"
  );
  writeFileSync(new URL("meta.json", dir), JSON.stringify({ id: name, name }, null, 2) + "\n");
  return total;
}
