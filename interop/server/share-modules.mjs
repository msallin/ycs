// A module-resolution hook (module.register): the SignalR provider's imports of yjs, lib0 and y-protocols resolve to
// this directory's copies, so the driver and the provider share one Yjs (two copies break instanceof and warn).
const signalr = new URL('../signalr/', import.meta.url).href
const here = new URL('./package.json', import.meta.url).href

export const resolve = (specifier, context, next) =>
  context.parentURL?.startsWith(signalr) && !context.parentURL.includes('/node_modules/@microsoft/') &&
  /^(yjs|lib0|y-protocols)(\/|$)/.test(specifier)
    ? next(specifier, { ...context, parentURL: here })
    : next(specifier, context)
