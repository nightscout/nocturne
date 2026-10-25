/**
 * Prints when each beat of a film is on screen, and the `--at` list to snapshot them all:
 * `node scripts/beats.js [--features] <name>`. A rest is sampled 1.6s after the cursor arrives, a scrub 80% of
 * the way through, so the frame shows the tooltip the note describes.
 */
import { schedule } from "../src/tour/timing.js";
import { kindOf } from "./kind.js";

const kind = kindOf(process.argv.slice(2));
const [name] = kind.names;
if (!kind.list[name]) {
  console.error(`usage: node scripts/beats.js [--features] <${Object.keys(kind.list).join("|")}>`);
  process.exit(1);
}

const film = await kind.loadFilm(name);
const { planned, outro, total } = schedule(film);
const times = [];
planned.forEach((scene, i) => {
  console.log(`${film.scenes[i].heading}`);
  scene.beats.forEach((beat, j) => {
    const at = +(beat.scrubEnd > beat.arrive ? beat.arrive + (beat.scrubEnd - beat.arrive) * 0.8 : beat.arrive + 1.6).toFixed(2);
    times.push(at);
    console.log(`  ${at.toFixed(2).padStart(6)}s  ${film.scenes[i].beats[j].note}`);
  });
});
console.log(`logo from ${(outro + 1.4).toFixed(2)}s, ends ${total}s`);
console.log(`\n--at ${times.join(",")}`);
