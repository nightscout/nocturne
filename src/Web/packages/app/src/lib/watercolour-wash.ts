import type { CropWindow } from '@nocturne/watercolour';

// Measured in host sizes: the wash is 1.92 hosts wide and 3.03 tall, and the host sits 0.46 of its
// width in from the wash's left and one height down from its top. That keeps the wash's dried
// perimeter outside glucose tiles and carb bars.
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
