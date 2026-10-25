import type { ArtworkId, PaletteId } from '@nocturne/watercolour';

export interface DocsHero {
  artwork: ArtworkId;
  palette: PaletteId;
  /** False parks the hero in the gutter instead of flowing text around it. */
  wrap?: boolean;
}

/**
 * The hero painting for each docs page, keyed by the slug under `/docs`.
 *
 * Pages whose heading is followed by a card rather than prose carry
 * `wrap: false`; a float displaces line boxes, not block backgrounds.
 */
export const DOCS_HERO: Record<string, DocsHero> = {
  // Landing and installation
  '': { artwork: 'paint-palette', palette: 'dusk', wrap: false },
  'getting-started': { artwork: 'sunrise', palette: 'ember' },
  installation: { artwork: 'world-globe', palette: 'water' },
  'installation/docker-compose': { artwork: 'overlapping-shapes', palette: 'dusk' },
  'installation/portainer': { artwork: 'suitcase', palette: 'dusk' },
  'installation/oracle-cloud': { artwork: 'chat-bubble', palette: 'water' },
  'installation/byo-postgres': { artwork: 'key', palette: 'ember', wrap: false },
  'installation/reverse-proxy': { artwork: 'shield', palette: 'water' },

  // Authentication
  authentication: { artwork: 'people-group', palette: 'dusk' },
  'authentication/passkeys': { artwork: 'crescent-moon', palette: 'moonlight' },
  'authentication/google': { artwork: 'magnifying-glass', palette: 'water' },
  'authentication/github': { artwork: 'github-mark', palette: 'slate' },
  'authentication/oidc': { artwork: 'plug', palette: 'slate' },
  'authentication/request-membership': { artwork: 'exclamation-mark', palette: 'ember' },

  // Sharing and privacy
  sharing: { artwork: 'heart', palette: 'ember' },
  'sharing/public-link': { artwork: 'moonlit-shoreline', palette: 'moonlight' },
  'sharing/members': { artwork: 'footprints', palette: 'moss' },
  'sharing/guest-links': { artwork: 'stopwatch', palette: 'slate' },
  'sharing/clock': { artwork: 'clock', palette: 'slate' },

  // Food and carbs
  food: { artwork: 'apple', palette: 'moss' },
  'food/carbs': { artwork: 'pizza-slice', palette: 'ember' },
  'food/meals': { artwork: 'calendar', palette: 'moonlight' },
  'food/catalog': { artwork: 'report-pages', palette: 'slate' },
  'food/deduplication': { artwork: 'confirmation-mark', palette: 'moss' },

  // Bots
  bots: { artwork: 'chat-bubble', palette: 'water' },
  'bots/discord': { artwork: 'people-group', palette: 'dusk' },
  'bots/telegram': { artwork: 'phone', palette: 'slate' },
  'bots/slack': { artwork: 'linked-rings', palette: 'dusk' },
  'bots/whatsapp': { artwork: 'chat-bubble', palette: 'water' },

  // Devices, data and operations
  'connecting-apps': { artwork: 'connected-shores', palette: 'water' },
  'connecting-apps/device-flow': { artwork: 'blood-drop', palette: 'ember' },
  'alerts/email': { artwork: 'alarm-bell', palette: 'moonlight' },
  configuration: { artwork: 'spanner', palette: 'slate', wrap: false },
  observability: { artwork: 'distant-mountains', palette: 'slate' },
  sdks: { artwork: 'magnifying-glass', palette: 'water' },
  'windows-widget': { artwork: 'phone', palette: 'slate' },
  'windows-widget-privacy': { artwork: 'shield', palette: 'water' },
};
