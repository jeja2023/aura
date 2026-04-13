# Kubernetes 示例（指标与网络）

本目录提供**示例清单**，部署前请将占位命名空间、标签与主机名替换为实际环境值。

## 为何 Kubernetes NetworkPolicy 难以单独「只封 /metrics」

`NetworkPolicy` 基于 **L3/L4**（Pod、端口、协议），**不能**按 HTTP 路径放行或拒绝。若仅开放业务端口给 Ingress、同时让 Prometheus 抓取 `/metrics`，常见做法如下。

### 方案 A：Ingress 层拒绝公网访问 `/metrics`（本仓库示例）

在对外 Ingress（如 ingress-nginx）上使用注解或 `server-snippet`，对路径 `/metrics` 返回 **403** 或 **404**；Prometheus 使用**集群内 Service** 直连 Pod 或内部 Ingress，不经过该规则。

见 **`ingress-nginx-deny-public-metrics.example.yaml`**。

### 方案 B：服务网格

使用 Istio / Linkerd 等的 **AuthorizationPolicy** 按路径、JWT、源身份细粒度控制。

### 方案 C：独立监听指标端口（应用改造）

在应用中单独绑定内网端口仅暴露 Prometheus 端点，由 NetworkPolicy 只允许监控命名空间访问该端口（本仓库当前为单端口 **5000**，未采用此模式）。

## 示例文件说明

| 文件 | 说明 |
|------|------|
| `ingress-nginx-deny-public-metrics.example.yaml` | 对外域名上拒绝 `GET /metrics`，业务路径不受影响。 |
| `network-policy-api.example.yaml` | 默认拒绝入站，放行同命名空间、Ingress 控制器与监控命名空间（可按需收紧）。 |
