
            
            /** @typedef {() => Promise<import("wuchale/runtime").CatalogModule>} CatalogMod */
            /** @type {{[locale: string]: CatalogMod[]}} */
            const catalogs = {en: [() => import('./main.0.en.compiled.js')],es: [() => import('./main.0.es.compiled.js')],fr: [() => import('./main.0.fr.compiled.js')],de: [() => import('./main.0.de.compiled.js')],it: [() => import('./main.0.it.compiled.js')],pt: [() => import('./main.0.pt.compiled.js')],nl: [() => import('./main.0.nl.compiled.js')],ru: [() => import('./main.0.ru.compiled.js')],zh: [() => import('./main.0.zh.compiled.js')],ja: [() => import('./main.0.ja.compiled.js')],ko: [() => import('./main.0.ko.compiled.js')]}
            export const loadCatalog = (/** @type {number} */ loadID, /** @type {string} */ locale) => {
                return /** @type {CatalogMod} */ (/** @type {CatalogMod[]} */ (catalogs[locale])[loadID])()
            }
            export const loadCount = 1
            // not essential. in case it is needed and for debugging
            export const patterns = ["js"]
        