// 文件：前端冒烟静态服务（server.js） | File: Static server for smoke tests
const http = require("http");
const path = require("path");
const fs = require("fs/promises");

const root = path.resolve(__dirname, "../..");
const port = Number(process.env.PORT || 4173);

function contentTypeByExt(ext) {
  switch (ext) {
    case ".html":
      return "text/html; charset=utf-8";
    case ".css":
      return "text/css; charset=utf-8";
    case ".js":
      return "application/javascript; charset=utf-8";
    case ".svg":
      return "image/svg+xml";
    case ".json":
      return "application/json; charset=utf-8";
    case ".png":
      return "image/png";
    case ".jpg":
    case ".jpeg":
      return "image/jpeg";
    case ".webp":
      return "image/webp";
    case ".woff":
      return "font/woff";
    case ".woff2":
      return "font/woff2";
    default:
      return "application/octet-stream";
  }
}

function mapPrettyRoute(urlPath) {
  if (urlPath === "/" || urlPath === "") return "/login/";
  if (urlPath.endsWith("/")) {
    const dir = urlPath.replace(/\/+$/, "");
    const name = dir.split("/").filter(Boolean).pop() || "index";
    return `${dir}/${name}.html`;
  }
  return urlPath;
}

async function readFileSafe(filePath) {
  try {
    const buf = await fs.readFile(filePath);
    return { ok: true, buf };
  } catch {
    return { ok: false, buf: null };
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host || "127.0.0.1"}`);
  if (url.pathname === "/healthz") {
    res.writeHead(200, { "Content-Type": "text/plain; charset=utf-8" });
    res.end("ok");
    return;
  }

  const mapped = mapPrettyRoute(url.pathname);
  const decoded = decodeURIComponent(mapped);
  const normalized = path.posix.normalize(decoded).replace(/^(\.\.(\/|\\|$))+/, "");
  const diskPath = path.join(root, normalized);

  const { ok, buf } = await readFileSafe(diskPath);
  if (!ok) {
    res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    res.end("not found");
    return;
  }

  res.writeHead(200, { "Content-Type": contentTypeByExt(path.extname(diskPath).toLowerCase()) });
  res.end(buf);
});

server.listen(port, "127.0.0.1", () => {
  console.log(`smoke server listening on http://127.0.0.1:${port}`);
});
