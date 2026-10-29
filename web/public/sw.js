/* Levity service worker: opens instantly, works with a flaky connection. Never touches the relay (WebSockets bypass it). */
const CACHE = 'levity-shell-v1';
const SHELL = ['/', '/manifest.webmanifest', '/icon-192.png', '/icon-512.png', '/apple-touch-icon.png', '/favicon-32.png'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  const url = new URL(req.url);
  if (req.method !== 'GET' || url.origin !== self.location.origin) return;

  // Page loads: network first so a new deploy shows up, cached shell when offline.
  if (req.mode === 'navigate') {
    event.respondWith(fetch(req).then((res) => {
      const copy = res.clone();
      caches.open(CACHE).then((c) => c.put('/', copy));
      return res;
    }).catch(() => caches.match('/')));
    return;
  }

  // Hashed build assets and icons: cache first.
  event.respondWith(caches.match(req).then((hit) => hit || fetch(req).then((res) => {
    if (res.ok) { const copy = res.clone(); caches.open(CACHE).then((c) => c.put(req, copy)); }
    return res;
  })));
});

// Push: the message has no text on purpose. Always show something (iOS cancels subscriptions that stay silent), then open the app on tap.
self.addEventListener('push', (event) => {
  event.waitUntil((async () => {
    // The sound itself is the phone's own notification sound (web push cannot pick a custom one); asking for it
    // explicitly, with a vibration and a re-alert for each new notice, is everything the page can do.
    await self.registration.showNotification('Levity has news', {
      body: 'Open Levity to see it.',
      icon: '/icon-192.png',
      badge: '/favicon-32.png',
      tag: 'levity-news',   // a newer notice replaces an older one instead of piling up
      renotify: true,       // ...and still makes a sound again
      silent: false,
      vibrate: [120, 60, 120],
    });
    // An open window plays Levity's own chime as well.
    const open = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const c of open) c.postMessage({ type: 'push-received' });
  })());
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((list) => {
    for (const c of list) if ('focus' in c) return c.focus();
    return self.clients.openWindow('/');
  }));
});
