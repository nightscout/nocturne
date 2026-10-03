import type { CropWindow } from '@nocturne/watercolour';

// The dried perimeter stays outside glucose tiles and carb bars.
export const washInterior: CropWindow = {
  x: 0.46 / 1.92,
  y: 1 / 3.03,
  width: 1 / 1.92,
  height: 1 / 3.03,
};
