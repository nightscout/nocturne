import type { CropWindow } from '@nocturne/watercolour';

// The wash's size and the host's offset into it, in multiples of the host's own width and height,
// chosen so the wash's dried perimeter stays outside glucose tiles and carb bars.
const WASH_WIDTH_HOSTS = 1.92;
const WASH_HEIGHT_HOSTS = 3.03;
const HOST_LEFT_HOSTS = 0.46;
const HOST_TOP_HOSTS = 1;

export const washInterior: CropWindow = {
  x: HOST_LEFT_HOSTS / WASH_WIDTH_HOSTS,
  y: HOST_TOP_HOSTS / WASH_HEIGHT_HOSTS,
  width: 1 / WASH_WIDTH_HOSTS,
  height: 1 / WASH_HEIGHT_HOSTS,
};
