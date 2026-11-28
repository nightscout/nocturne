/** Shared by the column-label row and every compact row. From @2xl the cells sit on this grid; below it a row is two lines and the label row is hidden. */
export const ROW_GRID =
  "px-3 py-1 @2xl:grid @2xl:items-center @2xl:gap-x-4 @2xl:grid-cols-[minmax(0,1fr)_9rem_6.5rem_3rem_8rem] @4xl:grid-cols-[minmax(0,1fr)_9rem_6.5rem_10rem_3rem_8rem]";

/** The Review lens's counterpart of {@link ROW_GRID}: person, time in range, change, [distribution], GMI, CV, wear. */
export const REVIEW_ROW_GRID =
  "px-3 py-1 @2xl:grid @2xl:items-center @2xl:gap-x-4 @2xl:grid-cols-[minmax(0,1fr)_9rem_3.5rem_3.5rem_3rem_3.5rem] @4xl:grid-cols-[minmax(0,1fr)_9rem_3.5rem_10rem_3.5rem_3rem_3.5rem]";

/**
 * On every compact row's item. A row off screen skips style and layout, so a sort that moves all 300 rows restyles
 * only the rows on screen.
 */
export const ROW_ITEM = "[content-visibility:auto] [contain-intrinsic-size:auto_3rem]";
