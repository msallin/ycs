// Polling helpers shared by the drivers.

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))

// Resolves with the milliseconds it took once condition() holds; rejects after ms.
export const waitFor = (what, condition, ms) => new Promise((resolve, reject) => {
  const started = Date.now()
  const poll = () => {
    if (condition()) {
      resolve(Date.now() - started)
    } else if (Date.now() - started > ms) {
      reject(new Error(`timed out after ${ms} ms: ${what}`))
    } else {
      setTimeout(poll, 10)
    }
  }
  poll()
})
