import dotsUrl from "./assets/dots.png";
import dropUrl from "./assets/drop.png";
import moonUrl from "./assets/moon.png";

/** How long the animation runs; the mark is settled from about 3.2s and holds after that. */
export const LOGO_DURATION = 6;

const CSS = `
.nocturne-logo {
  position: absolute;
  inset: 0;
  overflow: hidden;
  background: #060d1e;
}
.nocturne-logo * {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}
.nocturne-logo .bg-glow {
  position: absolute;
  width: 880px;
  height: 880px;
  border-radius: 50%;
  background: radial-gradient(circle, rgba(36, 60, 160, 0.22) 0%, rgba(6, 13, 30, 0) 68%);
  top: 50%;
  left: 50%;
  transform: translate(-50%, -52%);
}
.nocturne-logo .ghost-grid {
  position: absolute;
  inset: 0;
  opacity: 0.03;
  background-image:
    linear-gradient(rgba(130, 160, 255, 1) 1px, transparent 1px),
    linear-gradient(90deg, rgba(130, 160, 255, 1) 1px, transparent 1px);
  background-size: 72px 72px;
}
.nocturne-logo .reg-mark {
  position: absolute;
  width: 22px;
  height: 22px;
  opacity: 0.13;
}
.nocturne-logo .reg-mark::before,
.nocturne-logo .reg-mark::after {
  content: "";
  position: absolute;
  background: #8aaafe;
}
.nocturne-logo .reg-mark::before {
  width: 100%;
  height: 1px;
  top: 50%;
}
.nocturne-logo .reg-mark::after {
  width: 1px;
  height: 100%;
  left: 50%;
}
.nocturne-logo .reg-tl { top: 52px; left: 52px; }
.nocturne-logo .reg-tr { top: 52px; right: 52px; }
.nocturne-logo .reg-bl { bottom: 52px; left: 52px; }
.nocturne-logo .reg-br { bottom: 52px; right: 52px; }
.nocturne-logo .vignette {
  position: absolute;
  inset: 0;
  background: radial-gradient(ellipse 82% 82% at 50% 50%, transparent 44%, rgba(3, 7, 18, 0.58) 100%);
  pointer-events: none;
}
/* The mark is laid out on a 1080px square, centred in whatever frame holds it. */
.nocturne-logo .mark {
  position: absolute;
  left: 50%;
  top: 50%;
  width: 1080px;
  height: 1080px;
  translate: -50% -50%;
}
.nocturne-logo .logo-layer {
  position: absolute;
  image-rendering: high-quality;
}
/* PSD logomark 1237x1416 at (2436, 547), scaled 0.438 to a 620px drop, origin at (269, 230). */
.nocturne-logo .drop {
  left: 347px;
  top: 230px;
  width: 379px;
  height: 620px;
  opacity: 0;
}
.nocturne-logo .moon {
  left: 349px;
  top: 469px;
  width: 382px;
  height: 387px;
  opacity: 0;
  transform-origin: 50% 50%;
}
.nocturne-logo .dots {
  left: 269px;
  top: 584px;
  width: 542px;
  height: 90px;
  opacity: 0;
}
.nocturne-logo .moon-bloom {
  position: absolute;
  left: 410px;
  top: 530px;
  width: 260px;
  height: 260px;
  border-radius: 50%;
  background: radial-gradient(circle, rgba(90, 128, 224, 0.28) 0%, rgba(90, 128, 224, 0) 70%);
  filter: blur(30px);
  opacity: 0;
  transform-origin: 50% 50%;
}
.nocturne-logo .wordmark,
.nocturne-logo .url {
  position: absolute;
  left: 0;
  width: 100%;
  text-align: center;
  font-family: "Montserrat", sans-serif;
  opacity: 0;
}
.nocturne-logo .wordmark {
  top: 875px;
  font-weight: 300;
  font-size: 80px;
  letter-spacing: 0.32em;
  color: #8ca8d4;
}
.nocturne-logo .url {
  top: 990px;
  font-weight: 400;
  font-size: 34px;
  letter-spacing: 0.12em;
  color: #6f86ad;
}
.nocturne-logo .drop-shadow {
  position: absolute;
  left: 400px;
  top: 830px;
  width: 280px;
  height: 18px;
  border-radius: 50%;
  background: rgba(26, 48, 128, 0.2);
  filter: blur(6px);
  opacity: 0;
}
`;

const MARKUP = `
<div class="bg-glow" data-layout-ignore></div>
<div class="ghost-grid" data-layout-ignore></div>
<div class="reg-mark reg-tl" data-layout-ignore></div>
<div class="reg-mark reg-tr" data-layout-ignore></div>
<div class="reg-mark reg-bl" data-layout-ignore></div>
<div class="reg-mark reg-br" data-layout-ignore></div>
<div class="mark">
  <div class="drop-shadow" data-layout-ignore></div>
  <img class="logo-layer drop" src="${dropUrl}" alt="" />
  <img class="logo-layer dots" src="${dotsUrl}" alt="" />
  <div class="moon-bloom" data-layout-ignore></div>
  <img class="logo-layer moon" src="${moonUrl}" alt="" />
  <div class="wordmark">nocturne</div>
  <div class="url"></div>
</div>
<div class="vignette" data-layout-ignore></div>
`;

/**
 * Builds the animated logomark into `parent` and returns it with its paused timeline, which runs
 * `LOGO_DURATION` seconds. `url`, when given, settles in under the wordmark.
 */
export function buildLogo(parent, { url } = {}) {
  if (!document.getElementById("nocturne-logo-css")) {
    const style = document.createElement("style");
    style.id = "nocturne-logo-css";
    style.textContent = CSS;
    document.head.append(style);
  }

  const root = document.createElement("div");
  root.className = "nocturne-logo";
  root.innerHTML = MARKUP;
  parent.append(root);
  const $ = (selector) => root.querySelector(selector);
  if (url) $(".url").textContent = url;

  const gsap = window.gsap;
  const tl = gsap.timeline({ paused: true });

  // Drop: a small circle clipped to the round bulb scales up while the clip opens to reveal the tip.
  tl.fromTo(
    $(".drop"),
    { opacity: 1, scale: 0.4, clipPath: "circle(18% at 50% 62%)", transformOrigin: "50% 62%" },
    { opacity: 1, scale: 1, clipPath: "circle(75% at 50% 50%)", transformOrigin: "50% 62%", duration: 1, ease: "expo.out" },
    0.15
  );
  tl.fromTo($(".drop-shadow"), { opacity: 0 }, { opacity: 1, duration: 0.55, ease: "power2.out" }, 0.6);

  // Moon: a waxing crescent, drawn as the moon's box minus a shadow circle (evenodd) that shrinks
  // and slides to the upper right, the way the earth's shadow recedes. The box is 382x387; the
  // shadow starts covering all of it.
  const moon = $(".moon");
  const shadow = { cx: 191, cy: 194, r: 230 };
  const clip = ({ cx, cy, r }) =>
    `path(evenodd,"M0 0L382 0L382 387L0 387Z M${cx} ${cy - r} A${r} ${r} 0 1 1 ${cx} ${cy + r} A${r} ${r} 0 1 1 ${cx} ${cy - r}Z")`;
  const updateMoonClip = () => {
    moon.style.clipPath = clip(shadow);
  };
  tl.fromTo(moon, { opacity: 0 }, { opacity: 1, duration: 0.3, ease: "power2.out", onStart: updateMoonClip }, 0.15);
  tl.fromTo(
    shadow,
    { cx: 191, cy: 194, r: 230 },
    { cx: 255, cy: 165, r: 135, duration: 1.1, ease: "power2.inOut", onUpdate: updateMoonClip },
    0.2
  );
  // The crescent's own artwork carries its shape once revealed, so the clip comes off.
  tl.set(moon, { clipPath: "none" }, 1.35);
  tl.fromTo(
    $(".moon-bloom"),
    { opacity: 0, scale: 0.55 },
    { opacity: 1, scale: 1, duration: 0.9, ease: "power2.out", overwrite: "auto" },
    0.4
  );

  tl.fromTo($(".dots"), { opacity: 0, clipPath: "inset(0 100% 0 0)" }, { opacity: 1, clipPath: "inset(0 0% 0 0)", duration: 0.65, ease: "expo.out" }, 1.85);
  tl.fromTo($(".wordmark"), { opacity: 0, filter: "blur(18px)" }, { opacity: 1, filter: "blur(0px)", duration: 0.9, ease: "power2.out" }, 2.3);
  if (url) tl.fromTo($(".url"), { opacity: 0, y: 12 }, { opacity: 1, y: 0, duration: 0.8, ease: "power2.out" }, 3.1);

  return { root, tl };
}
