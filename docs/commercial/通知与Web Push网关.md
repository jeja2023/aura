# 通知与 Web Push 网关运行手册

Aura 的通知权威状态保存在 PostgreSQL。通知 Worker 使用租约领取任务，并通过站内、Webhook、邮件、短信、协作工具、工单或 `web_push` 通道发送；失败按指数退避重试，达到上限后进入可观察的终态。

## Web Push 边界

浏览器只向 Aura 登记 Push API 返回的 endpoint、P-256 公钥和 auth secret。Aura 不把订阅、API 响应、令牌或媒体写入 Service Worker 缓存。通知点击只能导航到同源 `/workbench/` 深链，页面加载后重新执行登录、租户和对象授权。

VAPID 私钥只存在于批准的自托管 Web Push 网关。Aura 配置 `CommercialProduct:Mobile:WebPushPublicKey` 向浏览器公开对应公钥；私钥禁止进入前端、Aura 配置响应、数据库通知正文和日志。

## 网关配置

1. 在治理 API 中建立 channel=`web_push` 的通知通道配置，endpoint 指向通过统一出站 URL 策略的 HTTPS 网关，secretRef 指向网关访问令牌。
2. 建立 channel=`web_push` 的活动通知模板；模板只包含最小标题、正文和 Aura 工作台深链，不包含原始图片、特征向量或秘密。
3. 将 `CommercialProduct__Mobile__WebPushPublicKey`（Docker 使用 `COMMERCIAL_WEB_PUSH_PUBLIC_KEY`）设置为 VAPID 公钥并重启 API。
4. 用户在工作台主动授予浏览器通知权限后，Aura 才保存订阅。

Aura 向网关发送的请求包含通知 ID、租户/案件/事件引用、已脱敏正文、幂等键、追踪 ID、providerOptions，以及目标用户全部活动订阅：

```json
{
  "notificationId": 42,
  "channel": "web_push",
  "recipient": "operator-a",
  "content": "案件 CASE-2026-001 已升级",
  "subscriptions": [
    {
      "endpoint": "https://push.example/subscription",
      "keys": { "p256dh": "...", "auth": "..." }
    }
  ]
}
```

网关必须使用 `Idempotency-Key` 去重，对每个订阅执行标准 Web Push 加密和 VAPID 签名，并返回 2xx。可选响应 `receiptId` 会写入 Aura 通知投递记录。订阅返回 404/410 时，现场网关应返回稳定错误并由管理员撤销失效订阅；不得在日志中输出完整 endpoint 或密钥。

## 验收

- 目标桌面和移动浏览器完成订阅、接收、点击、登录过期和跨租户对象拒绝测试。
- 主通道故障能够按租户策略切换备用通道，重复请求不产生重复可见通知。
- 网关证书、VAPID 密钥轮换、404/410 订阅回收、速率限制和故障恢复均有证据 URI。
- 未配置公钥、未授权通知或没有活动订阅时必须明确失败，禁止报告为已送达。

