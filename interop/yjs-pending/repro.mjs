import * as Y from 'yjs'
const us = ["AQGC34r0DgGE1ZKNWQABdQA=","AQL+lNwkBCcBA21hcAJrMAEoAP6U3CQEAXYBfacGAA==","AQPfkaCADACI/pTcJAIBfZQJh9+RoIAMAAEoAN+RoIAMAQF2AXgA","AQHVko1ZAAQBBHRleHQBagA=","AQGC34r0DgAoAQNtYXACazEBfgA=","AQH+lNwkAAgBBWFycmF5A3UCfQJ3BHRyYW12AgFrfQIBc3cEc2dhdX27AQA=","AQH+lNwkAygA35GggAwBAmszAX2WAwH2maWoBAEBAQ=="].map(s => new Uint8Array(Buffer.from(s, 'base64')))
const run = order => { const d = new Y.Doc(); for (const i of order) Y.applyUpdate(d, us[i]); return [d.store.pendingStructs?.missing, Y.decodeStateVector(Y.encodeStateVector(d))] }
console.log('this order:', run([0,1,2,3,4,5,6]))
console.log('reversed :', run([6,5,4,3,2,1,0]))
