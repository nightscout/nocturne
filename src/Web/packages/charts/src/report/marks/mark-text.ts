/** The props a mark's words take, spread onto layerchart's Text: a class on the page, a fill and a font on a canvas. */
export interface TextProps {
  class?: string;
  fill?: string;
  style?: string;
}

/** A plot's word styles: muted for axis and lane names, strong for a marker's amount. */
export interface MarkText {
  muted: TextProps;
  strong: TextProps;
}
