import { currentRuntime } from 'wuchale/load-utils/server'
import { loadCatalog, loadCount } from './.wuchale/main.proxy.sync.js'

const key = 'main'

export { key, loadCatalog, loadCount } // for hooks.server.{js,ts}

// for non-reactive
export const getRuntime = (loadID = 0) => currentRuntime(key, loadID)

// same function, only will be inside $derived when used
export const getRuntimeRx = getRuntime
