const CACHE = "aura-workbench-v2";
const SHELL = [
  "/workbench/", "/workbench/workbench.html", "/workbench/workbench.css", "/workbench/workbench.js",
  "/workbench/manifest.webmanifest", "/common/theme.css", "/common/shell.css", "/common/forms.css",
  "/common/theme-pref.js", "/common/shell.js", "/common/favicon.svg"
];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key)))).then(() => self.clients.claim()));
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== "GET" || url.origin !== self.location.origin || url.pathname.startsWith("/api/") || url.pathname.startsWith("/storage/")) return;
  event.respondWith(fetch(request).then((response) => {
    if (response.ok && SHELL.includes(url.pathname)) caches.open(CACHE).then((cache) => cache.put(request, response.clone()));
    return response;
  }).catch(() => caches.match(request).then((cached) => cached || caches.match("/workbench/workbench.html"))));
});

function notificationTarget(value) {
  try {
    const url = new URL(value || "/workbench/", self.location.origin);
    if (url.origin !== self.location.origin || url.pathname.replace(/\/+$/, "/") !== "/workbench/") return "/workbench/";
    return `${url.pathname}${url.search}`;
  } catch {
    return "/workbench/";
  }
}

self.addEventListener("push", (event) => {
  let payload = {};
  try { payload = event.data ? event.data.json() : {}; } catch { payload = {}; }
  const title = String(payload.title || "Aura 待办提醒").slice(0, 120);
  const body = String(payload.body || "你有一项新的现场处置任务").slice(0, 300);
  event.waitUntil(self.registration.showNotification(title, {
    body,
    icon: "/common/favicon.svg",
    badge: "/common/favicon.svg",
    tag: String(payload.tag || payload.notificationId || "aura-workbench").slice(0, 128),
    renotify: false,
    data: { href: notificationTarget(payload.path || payload.href) }
  }));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const href = notificationTarget(event.notification.data?.href);
  event.waitUntil(self.clients.matchAll({ type: "window", includeUncontrolled: true }).then(async (clients) => {
    const existing = clients.find((client) => new URL(client.url).pathname.replace(/\/+$/, "/") === "/workbench/");
    if (existing) {
      if ("navigate" in existing) await existing.navigate(href);
      return existing.focus();
    }
    return self.clients.openWindow(href);
  }));
});
