/* 文件：资源页脚本（campus.js） | File: Campus Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const treeWrapEl = document.getElementById("treeWrap");
const treeRootEl = document.getElementById("treeRoot");
const treeStatsEl = document.getElementById("treeStats");
const treeKeywordEl = document.getElementById("treeKeyword");
const expandAllBtn = document.getElementById("expandAllBtn");
const collapseAllBtn = document.getElementById("collapseAllBtn");
let latestTreeData = [];

/** 成功提示自动消失定时器 */
let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;

function clearSuccessStatusTimer() {
  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }
}

function hideResult() {
  if (!resultEl) return;
  resultEl.textContent = "";
  resultEl.hidden = true;
  resultEl.classList.remove("is-error");
}

function deriveMessage(data) {
  if (typeof data === "string") return data;
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") return data.msg;
    if (Array.isArray(data.data)) return `共 ${data.data.length} 条结果`;
    return "操作完成";
  }
  return String(data ?? "");
}

function isErrorPayload(data, message) {
  if (data && typeof data === "object" && typeof data.code === "number") {
    return data.code !== 0;
  }
  if (typeof message === "string") {
    return /失败|错误|异常|超时|拒绝|未授权|无权|禁止|非法|无效|无法|不能|不存在|已过期|已失效/.test(message);
  }
  return false;
}

function setResult(data) {
  if (!resultEl) return;

  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    clearSuccessStatusTimer();
    hideResult();
    return;
  }

  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);

  clearSuccessStatusTimer();
  resultEl.textContent = message;
  resultEl.hidden = false;
  resultEl.classList.toggle("is-error", isError);

  if (!isError) {
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      hideResult();
    }, SUCCESS_STATUS_MS);
  }
}

function normalizeNodes(data) {
  if (!Array.isArray(data)) return [];
  return data.map((node) => ({
    nodeId: node?.nodeId ?? "-",
    parentId: node?.parentId ?? null,
    levelType: String(node?.levelType ?? "未知层级"),
    nodeName: String(node?.nodeName ?? "未命名节点"),
    children: normalizeNodes(node?.children ?? [])
  }));
}

function getLevelBadgeClass(levelType) {
  const text = String(levelType ?? "").toLowerCase();
  if (text.includes("campus")) return "is-campus";
  if (text.includes("building")) return "is-building";
  if (text.includes("floor")) return "is-floor";
  if (text.includes("room")) return "is-room";
  return "";
}

function countTreeStats(list, stats = { total: 0, campus: 0, building: 0, floor: 0, room: 0 }) {
  const rows = Array.isArray(list) ? list : [];
  rows.forEach((node) => {
    const level = String(node?.levelType ?? "").toLowerCase();
    stats.total += 1;
    if (level.includes("campus")) stats.campus += 1;
    else if (level.includes("building")) stats.building += 1;
    else if (level.includes("floor")) stats.floor += 1;
    else if (level.includes("room")) stats.room += 1;
    countTreeStats(node?.children ?? [], stats);
  });
  return stats;
}

function renderTreeStats(list) {
  if (!treeStatsEl) return;
  const stats = countTreeStats(list);
  treeStatsEl.innerHTML = [
    `<span class="tree-stat-chip">节点 ${stats.total}</span>`,
    `<span class="tree-stat-chip">园区 ${stats.campus}</span>`,
    `<span class="tree-stat-chip">楼栋 ${stats.building}</span>`,
    `<span class="tree-stat-chip">楼层 ${stats.floor}</span>`,
    `<span class="tree-stat-chip">房间 ${stats.room}</span>`
  ].join("");
}

function keywordMatched(node, keyword) {
  const target = `${node?.nodeName ?? ""} ${node?.levelType ?? ""} ${node?.nodeId ?? ""}`.toLowerCase();
  return target.includes(keyword);
}

function filterTreeNodes(list, keyword) {
  if (!keyword) return list;
  const rows = Array.isArray(list) ? list : [];
  return rows
    .map((node) => {
      const nextChildren = filterTreeNodes(node.children, keyword);
      if (keywordMatched(node, keyword) || nextChildren.length > 0) {
        return {
          ...node,
          children: nextChildren
        };
      }
      return null;
    })
    .filter(Boolean);
}

function getCurrentKeyword() {
  return String(treeKeywordEl?.value ?? "")
    .trim()
    .toLowerCase();
}

function setToggleExpanded(toggleEl, expanded) {
  if (!(toggleEl instanceof HTMLButtonElement)) return;
  toggleEl.classList.toggle("is-collapsed", !expanded);
  toggleEl.classList.toggle("is-expanded", expanded);
}

function renderTreeNodes(list, hostEl, options = {}, depth = 0) {
  if (!hostEl) return;
  hostEl.textContent = "";
  const keywordActive = Boolean(options.keywordActive);

  list.forEach((node) => {
    const li = document.createElement("li");
    li.className = "tree-node";
    li.dataset.depth = String(depth);

    const line = document.createElement("div");
    line.className = "tree-line";

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "tree-toggle";

    const hasChildren = Array.isArray(node.children) && node.children.length > 0;
    let childUl = null;

    if (hasChildren) {
      const levelTypeText = String(node.levelType ?? "").toLowerCase();
      const collapseRoomsByDefault = !keywordActive && levelTypeText.includes("floor");
      setToggleExpanded(toggle, !collapseRoomsByDefault);
      toggle.setAttribute("aria-label", `展开或收起${node.nodeName}`);
      childUl = document.createElement("ul");
      childUl.dataset.depth = String(depth + 1);
      renderTreeNodes(node.children, childUl, options, depth + 1);
      childUl.hidden = collapseRoomsByDefault;
      toggle.addEventListener("click", () => {
        const hidden = childUl.hidden;
        childUl.hidden = !hidden;
        setToggleExpanded(toggle, hidden);
      });
    } else {
      toggle.classList.add("is-leaf");
      toggle.disabled = true;
    }

    const name = document.createElement("span");
    name.className = "tree-name";
    name.textContent = node.nodeName;

    const levelBadge = document.createElement("span");
    levelBadge.className = `tree-level-badge ${getLevelBadgeClass(node.levelType)}`.trim();
    levelBadge.textContent = node.levelType;

    const meta = document.createElement("span");
    meta.className = "tree-meta";
    meta.textContent = `ID:${node.nodeId}`;

    line.append(toggle, levelBadge, name, meta);
    li.appendChild(line);
    if (childUl) li.appendChild(childUl);
    hostEl.appendChild(li);
  });
}

function renderTreeByKeyword() {
  if (!treeWrapEl || !treeRootEl) return;
  const keyword = getCurrentKeyword();
  const rows = filterTreeNodes(latestTreeData, keyword);
  if (rows.length === 0) {
    treeRootEl.textContent = "";
    treeWrapEl.hidden = true;
    setResult(keyword ? "未找到匹配节点" : "暂无资源树数据");
    return;
  }
  renderTreeNodes(rows, treeRootEl, { keywordActive: Boolean(keyword) });
  treeWrapEl.hidden = false;
}

function setTreeData(nodes) {
  if (!treeWrapEl || !treeRootEl) return;
  latestTreeData = normalizeNodes(nodes);
  renderTreeStats(latestTreeData);
  renderTreeByKeyword();
}

function getDirectChildList(nodeEl) {
  if (!(nodeEl instanceof HTMLElement)) return null;
  for (const child of nodeEl.children) {
    if (child instanceof HTMLUListElement) return child;
  }
  return null;
}

function setExpandState(expanded) {
  if (!treeRootEl) return;
  const childLists = treeRootEl.querySelectorAll(".tree-node > ul");
  if (childLists.length === 0) {
    setResult("当前无可展开节点");
    return;
  }
  childLists.forEach((el) => {
    el.hidden = !expanded;
  });
  treeRootEl.querySelectorAll(".tree-toggle").forEach((btn) => {
    if (btn.classList.contains("is-leaf")) return;
    const row = btn.closest(".tree-node");
    const child = getDirectChildList(row);
    if (!child) return;
    setToggleExpanded(btn, !child.hidden);
  });
}

async function load(options = {}) {
  setResult("");
  if (treeWrapEl) treeWrapEl.hidden = true;
  if (treeRootEl) treeRootEl.textContent = "";

  try {
    const res = await fetch(`${apiBase}/api/campus/tree`, {
      credentials: "include"
    });
    const data = await res.json();
    if (data?.code === 0) {
      setTreeData(data.data);
    }
    if (!options.silentSuccessToast || !data || data.code !== 0) {
      setResult(data);
    }
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
treeKeywordEl?.addEventListener("input", () => renderTreeByKeyword());
expandAllBtn?.addEventListener("click", () => setExpandState(true));
collapseAllBtn?.addEventListener("click", () => setExpandState(false));
void load({ silentSuccessToast: true });
