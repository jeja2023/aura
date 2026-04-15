/* 文件：楼层图 URL 规范化 | File: Floor image URL normalization */
/**
 * 将接口返回的楼层图纸路径转为页面可请求的绝对路径（相对站点根）。
 * 兼容仅存文件名、缺少前导斜杠、缺少 storage 前缀等历史或错误写入。
 */
function normalizeFloorImagePathToUrl(filePath, apiBase) {
  const base = apiBase ?? "";
  const raw = String(filePath ?? "").trim();
  if (!raw) return "";
  if (/^https?:\/\//i.test(raw)) return raw;
  let p = raw.replaceAll("\\", "/");
  if (p.startsWith("/uploads/floors/")) {
    p = `/storage${p}`;
  }
  if (!p.startsWith("/")) {
    if (p.startsWith("storage/")) {
      p = `/${p}`;
    } else if (p.startsWith("uploads/floors/")) {
      p = `/storage/${p}`;
    } else if (!p.includes("/")) {
      p = `/storage/uploads/floors/${p}`;
    } else {
      p = `/${p}`;
    }
  }
  return `${base}${p}`;
}
