// Enkel service worker som gjør sangerskjermen installerbar som app. Den mellomlagrer ingenting.
self.addEventListener('install',()=>self.skipWaiting());
self.addEventListener('activate',e=>e.waitUntil(self.clients.claim()));
self.addEventListener('fetch',()=>{});
