
            
            /** @typedef {() => Promise<import("wuchale/runtime").CatalogModule>} CatalogMod */
            /** @typedef {{[locale: string]: CatalogMod}} KeyCatalogs */
            /** @type {{[loadID: string]: KeyCatalogs}} */
            const catalogs = {js: {en: () => import('./main.main.en.compiled.js'),es: () => import('./main.main.es.compiled.js'),fr: () => import('./main.main.fr.compiled.js'),de: () => import('./main.main.de.compiled.js'),it: () => import('./main.main.it.compiled.js'),pt: () => import('./main.main.pt.compiled.js'),nl: () => import('./main.main.nl.compiled.js'),ru: () => import('./main.main.ru.compiled.js'),zh: () => import('./main.main.zh.compiled.js'),ja: () => import('./main.main.ja.compiled.js'),ko: () => import('./main.main.ko.compiled.js')}}
            export const loadCatalog = (/** @type {string} */ loadID, /** @type {string} */ locale) => {
                return /** @type {CatalogMod} */ (/** @type {KeyCatalogs} */ (catalogs[loadID])[locale])()
            }
            export const loadIDs = ['js']
        