# C++ SDK 真厂商对接接口契约与待接入点

## 一、目标

为后端 `POST /api/capture/sdk` 提供稳定、可版本化的厂商 SDK 上行契约，并标注当前未实装的接入点。

## 二、上行接口契约

- 接口：`POST /api/capture/sdk`
- Content-Type：`application/json`
- 鉴权：当前阶段使用平台登录鉴权（Bearer Token）；真厂商联调阶段建议增加设备级签名

### 2.1 请求体（V1）

```json
{
  "deviceId": 1001,
  "channelNo": 1,
  "timestamp": "2026-03-21T10:20:30+08:00",
  "imageBase64": "base64-encoded-image",
  "metadataJson": "{\"cameraCode\":\"A-1-01\",\"eventType\":\"person_pass\"}"
}
```

字段说明：

- `deviceId`：平台设备ID（必填）
- `channelNo`：通道号（必填）
- `timestamp`：抓拍时间（必填，ISO8601）
- `imageBase64`：图片Base64（必填，建议JPEG）
- `metadataJson`：扩展元数据（可选，JSON字符串）

### 2.2 响应体

```json
{
  "code": 0,
  "msg": "C++SDK抓拍接收成功",
  "data": {
    "captureId": 123,
    "deviceId": 1001,
    "channelNo": 1,
    "captureTime": "2026-03-21T10:20:30+08:00",
    "metadataJson": "{...}"
  }
}
```

## 三、兼容策略

- SDK 版本头：建议增加 `X-SDK-Version`
- 契约版本：建议增加 `schemaVersion`（例如 `1.0`）
- 时间字段统一按 ISO8601 解析，时区不丢失

## 四、待接入点（未实装）

- [ ] 厂商 SDK 原始事件字段映射表（事件码 -> 标准事件类型）
- [ ] SDK 图片二进制直传（`multipart/form-data`）与大图分片上传
- [ ] 设备级签名验签（替代或补充平台 Token）
- [ ] SDK 回调重放保护（`nonce + timestamp`）
- [ ] 失败回执协议（错误码分层：网络/验签/解析/落库/AI）
- [ ] 厂商心跳/状态通道与平台设备状态联动
- [ ] 批量事件上报（数组体）及幂等去重
- [ ] 链路压测基线（QPS、延迟、丢包重传策略）

## 五、联调建议步骤

1. 先以当前 JSON 契约打通单条事件。
2. 加入签名与重放保护，完成安全联调。
3. 引入批量上报和失败重试，完成稳定性联调。
4. 最后切换真实事件码映射并执行回归脚本。
