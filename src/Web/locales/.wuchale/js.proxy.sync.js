
            import * as _w_c_js_0_ from './main.main.en.compiled.js'
import * as _w_c_js_1_ from './main.main.es.compiled.js'
import * as _w_c_js_2_ from './main.main.fr.compiled.js'
import * as _w_c_js_3_ from './main.main.de.compiled.js'
import * as _w_c_js_4_ from './main.main.it.compiled.js'
import * as _w_c_js_5_ from './main.main.pt.compiled.js'
import * as _w_c_js_6_ from './main.main.nl.compiled.js'
import * as _w_c_js_7_ from './main.main.ru.compiled.js'
import * as _w_c_js_8_ from './main.main.zh.compiled.js'
import * as _w_c_js_9_ from './main.main.ja.compiled.js'
import * as _w_c_js_10_ from './main.main.ko.compiled.js'
            /** @typedef {import("wuchale/runtime").CatalogModule} CatalogMod */
            /** @typedef {{[locale: string]: CatalogMod}} KeyCatalogs */
            /** @type {{[loadID: string]: KeyCatalogs}} */
            const catalogs = {js: {en: _w_c_js_0_,es: _w_c_js_1_,fr: _w_c_js_2_,de: _w_c_js_3_,it: _w_c_js_4_,pt: _w_c_js_5_,nl: _w_c_js_6_,ru: _w_c_js_7_,zh: _w_c_js_8_,ja: _w_c_js_9_,ko: _w_c_js_10_}}
            export const loadCatalog = (/** @type {string} */ loadID, /** @type {string} */ locale) => {
                return /** @type {CatalogMod} */ (/** @type {KeyCatalogs} */ (catalogs[loadID])[locale])
            }
            export const loadIDs = ['js']
        