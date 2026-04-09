# 监控接口（UDP / 信令 / 在线数 / 配置）

本文档面向监控前端（例如 Vue3 + TypeScript），用于获取服务端的：

- UDP 指标：接收包数/字节数、以及按类型拆分（视频/位姿/控制/反馈/未知）
- 系统聚合指标：在线机器人/VR数量、信令收发速率（gRPC + WS）、以及 UDP 指标聚合
- 全局配置：当前环境/模式、广播开关、UDP 端口与 UDP 映射救援参数

## 重要说明

- 这些接口**只输出指标/配置**，不输出/不转发任何 UDP 负载内容。
- UDP 指标接口（`/api/monitor/udp/*`）默认仅在 **Development** 环境启用；如需在其它环境启用，请在配置中打开 `Monitoring:Enabled=true`。
- 系统聚合接口与配置接口当前未做鉴权；如要暴露到生产环境，建议加一层认证（例如反向代理鉴权/JWT/内网访问）。
- `pps/bps/perSecond` 是“上一秒窗口”的聚合值（由服务端内部定时器每 1 秒刷新一次）。

### 关于 UDP 转发的“尝试/成功/失败”

UDP 发送在应用层通常只能得到两类信息：

- **尝试发送**：我们调用了 `SendAsync`（或入队后由发送线程调用）。
- **发送成功/失败**：`SendAsync` 是否抛出 `SocketException`。

因此：

- `tx` 代表“发送尝试”（保持历史口径，便于和旧版前端兼容）。
- `txOk` 代表“发送成功”（更接近“确实从本机发出去”的含义）。
- `txFail` 代表“发送失败”（例如 `NoBufferSpaceAvailable` 表示本机发送缓冲/队列压力）。

> 注意：即使 `txOk` 增加，也不代表 VR 端一定收到（UDP 不保证送达）。但 `txFail/noBuffer` 能直接暴露“本机侧尖峰/缓冲耗尽”。

## 指标来源与分类规则

服务端对每个收到的 UDP 包做分类计数（按第 1 个字节 `prefix`）：

- 控制包：`'H'` 或 `'P'`（HELLO / PING，ASCII）
- 视频包：`0x01`
- 位姿包：`0x02`
- 反馈包：`0x03`
- 其它：unknown

> 注意：这是按“包首字节”分类，并不解析 RTP/自定义负载内容。

---

## 1) UDP 指标（Polling）

### GET /api/monitor/udp/stats

返回一次快照（JSON）。适合前端轮询。

**Response: `application/json`**

字段说明：

- `updatedUtc`: 服务端上次刷新“每秒窗口”的 UTC 时间（ISO 8601）
- `rx`: 服务端接收（收到）的 UDP 指标
  - `pps`: 上一秒窗口的包数（Packets Per Second）
  - `bps`: 上一秒窗口的字节数（Bytes Per Second）
  - `perSecond`: 上一秒窗口的分类计数（`control/video/pose/feedback/unknown`）
  - `totals`: 启动以来累计值（`packets/bytes/control/video/pose/feedback/unknown`）
- `tx`: 服务端发送（吐出）的 UDP 指标
  - `pps`: 上一秒窗口的发送包数
  - `bps`: 上一秒窗口的发送字节数
  - `perSecond`: 上一秒窗口的发送分类计数（`control/video/pose/feedback/unknown`）
  - `totals`: 启动以来累计发送值（`packets/bytes/control/video/pose/feedback/unknown`）

新增字段：

- `txOk`: 发送成功（`SendAsync` 未抛异常）
  - 字段结构与 `tx` 相同
- `txFail`: 发送失败（`SendAsync` 抛 `SocketException`）
  - `perSecond.noBuffer`: 上一秒 `SocketError.NoBufferSpaceAvailable` 次数
  - `totals.noBuffer`: 启动以来 `SocketError.NoBufferSpaceAvailable` 次数
- `txRetry`: 发送重试次数（当前实现：仅对 `NoBufferSpaceAvailable` 按配置重试）
  - `pps`: 上一秒重试次数
  - `totals.count`: 启动以来重试次数
- `forwarding.queueDrop`: 转发发送队列丢弃（队列满时 DropWrite）
  - `pps/bps`: 上一秒被丢弃的包/字节
  - `perSecond`: 上一秒按 `video/pose/feedback/unknown` 分类的丢弃数
  - `totals`: 启动以来累计

**Example**

```json
{
  "updatedUtc": "2025-12-31T12:34:56.789Z",
  "rx": {
    "pps": 1200,
    "bps": 983040,
    "perSecond": {
      "control": 2,
      "video": 900,
      "pose": 280,
      "feedback": 18,
      "unknown": 0
    },
    "totals": {
      "packets": 123456,
      "bytes": 987654321,
      "control": 120,
      "video": 90000,
      "pose": 30000,
      "feedback": 3336,
      "unknown": 0
    }
  },
  "tx": {
    "pps": 800,
    "bps": 655360,
    "perSecond": {
      "control": 2,
      "video": 600,
      "pose": 180,
      "feedback": 18,
      "unknown": 0
    },
    "totals": {
      "packets": 100000,
      "bytes": 800000000,
      "control": 120,
      "video": 70000,
      "pose": 25000,
      "feedback": 5000,
      "unknown": 0
    }
  }
  ,
  "txOk": {
    "pps": 780,
    "bps": 640000,
    "perSecond": { "control": 2, "video": 580, "pose": 180, "feedback": 18, "unknown": 0 },
    "totals": { "packets": 99900, "bytes": 799000000, "control": 120, "video": 69900, "pose": 25000, "feedback": 5000, "unknown": 0 }
  },
  "txFail": {
    "pps": 20,
    "bps": 15360,
    "perSecond": { "control": 0, "video": 20, "pose": 0, "feedback": 0, "unknown": 0, "noBuffer": 20 },
    "totals": { "packets": 100, "bytes": 76800, "control": 0, "video": 100, "pose": 0, "feedback": 0, "unknown": 0, "noBuffer": 100 }
  },
  "txRetry": {
    "pps": 20,
    "totals": { "count": 100 }
  },
  "forwarding": {
    "queueDrop": {
      "pps": 50,
      "bps": 40960,
      "perSecond": { "video": 50, "pose": 0, "feedback": 0, "unknown": 0 },
      "totals": { "packets": 500, "bytes": 409600, "video": 500, "pose": 0, "feedback": 0, "unknown": 0 }
    }
  }
}
```

---

## UDP 转发队列配置（可选）

服务端支持“按目标端点分桶的发送队列”（避免某一瞬间把积压包打成尖峰）。配置路径：

- `MediaServer:UdpForwarding`
  - `Enabled`：是否启用（默认 true）
  - `QueueCapacityPerTarget`：每个目标端点的队列容量（默认 2048，队列满则丢包并计入 `forwarding.queueDrop`）
  - `MaxPpsPerTarget`：每目标每秒最大包数（0 表示不限）
  - `MaxBpsPerTarget`：每目标每秒最大字节数（0 表示不限）
  - `RetryOnNoBuffer`：是否对 `NoBufferSpaceAvailable` 做重试（默认 true）
  - `MaxRetries`：最大重试次数（默认 1）
  - `RetryDelayMs`：重试延迟（默认 1ms）

---

## 2) UDP 指标（SSE）

### GET /api/monitor/udp/stream

**Response: `text/event-stream`**

- Event name: `udp`
- Data: JSON（结构与 `/api/monitor/udp/stats` 相同）

#### Query 参数

- `intervalMs`（可选）
  - 默认：`1000`
  - 范围：`250..10000`
  - 作用：控制推送间隔（毫秒）

#### 浏览器示例（原生）

```ts
// 推荐用 HTTPS 端口（默认 7777）。
// 说明：当前 7776 的 http 端点仅启用 Http2；浏览器的 EventSource 通常走 HTTP/1.1，可能导致连接失败。
const url = 'https://localhost:7777/api/monitor/udp/stream?intervalMs=1000'
const es = new EventSource(url)

es.addEventListener('udp', (evt) => {
  const data = JSON.parse((evt as MessageEvent).data)
  console.log('rx pps', data.rx.pps, 'rx video', data.rx.perSecond.video, 'tx pps', data.tx.pps)
})

es.onerror = (e) => {
  console.error('sse error', e)
}
```

> 如果你不指定事件名，也可以直接用 `es.onmessage`，但本接口用的是 `event: udp`，建议监听 `udp`。

---

---

## 3) 系统聚合指标（Polling）

### GET /api/monitor/system/stats

返回“系统聚合快照”（JSON），用于你在网页端渲染：

- 在线数量：机器人/VR/unknown，以及 pushConnectedOnline
- 信令收发速率：按消息类型统计（包含 gRPC + `/ws/proto`）
- UDP 指标：直接复用 UDP Snapshot
- 当前模式：是否处于全局广播（生效值）

**Response: `application/json`**

字段结构（简述）：

- `environment`: `{ name, isDevelopment, isProduction }`
- `mode.broadcastToAllEffective`: `bool`
- `online`:
  - `timeoutSeconds`: 在线判定窗口（当前固定为 30 秒）
  - `registered`: 已注册会话数（累计存活在内存中的 session）
  - `online`: 在线会话数（心跳未超时）
  - `onlineByRole`: `{ robot, vr, unknown }`
  - `pushConnectedOnline`: 在线且 push 通道已绑定的数量
- `signaling`:
  - `inboundPps`: `{ register, ping, pair, subscribe, listUnpaired, eventStream, wsBadProto, wsUnsupported, wsNoSession }`
  - `outboundPps`: `{ registerResponse, pingAck, pairResponse, subscribeResponse, listUnpairedResponse, wsError, events, pairEvents, systemCommands }`
  - `totals`: 同上结构的累计值
- `udp`: UDP 指标快照（同 `/api/monitor/udp/stats`）

---

## 4) 全局配置接口（Polling）

### GET /api/system/config

返回当前环境/模式与“安全的配置字段”（不包含证书路径/密码等敏感信息）。

**Response: `application/json`**

字段结构（简述）：

- `environment`: `{ name, isDevelopment, isProduction }`
- `mode`:
  - `broadcastToAllEffective`: `bool`（实际生效值：非 Development 强制为 false）
  - `broadcastToAllConfigured`: `bool | null`（Development 下返回配置值，否则为 null）
- `session.timeoutSeconds`: 当前在线判定/清理窗口（当前固定为 30）
- `mediaServer`:
  - `udpPort`
  - `udpControlTimeoutSeconds`
  - `udpRescueCooldownSeconds`
  - `udpMaxRescues`

---

## 5) 会话列表与详情（Polling）

用于前端“设备列表页 + 详情页”。建议列表只加载粗略信息，点击某个 session 再按 `sessionId` 拉详情。

### GET /api/monitor/sessions

返回会话粗略列表。

#### Query 参数

- `role`（可选）：`robot | vr | unknown`
- `onlineOnly`（可选，默认 `false`）：是否只返回在线会话

**Response: `application/json`**

- `timeoutSeconds`: 在线判定窗口（当前固定为 30 秒）
- `items`: 列表项（粗略信息）
  - `sessionId`
  - `deviceId`
  - `role`
  - `online`
  - `pushConnected`
  - `hasUdpEndpoint`
  - `pairedSessionId`

### GET /api/monitor/sessions/{sessionId}

按 `sessionId` 获取会话详情。

**Response: `application/json`**

- `detail.sessionId/deviceId/role`
- `detail.online/pushConnected`
- `detail.client.ip/port`
- `detail.udp.endpoint`（形如 `1.2.3.4:5678`）
- `detail.udp.lastControlUtc/lastDataUtc`
- `detail.heartbeat.lastHeartbeatUtc`
- `detail.pairing.pairedSessionId/pairedDeviceId`
- `detail.subscriptions.subscriberCount`
- `detail.subscriptions.subscribers[]`（每个订阅者的资源掩码）

额外：订阅关系（Publisher 侧 meta）：

- `detail.subscriptionMeta.subscribers[]`：作为发布者时，我的订阅者的 rich meta（含 `targetBitrateKbps/subscriberBandwidthKbps/spsBytesLength/ppsBytesLength/lastUpdatedUtc` 等）
- `detail.subscriptionMeta.subscribedTo[]`：作为订阅者时，我订阅了哪些发布者（从全局 publisher meta 反查）

额外：UDP 转发统计（高性能路径，按 "publisherSessionId -> targetSessionId" 预绑定计数器）：

- `detail.forwarding.updatedUtc`：转发统计每秒窗口的刷新时间（UTC）
- `detail.forwarding.outboundTo[]`：仅列出“配对者 + 当前订阅者”两类目标
  - `targetSessionId/targetDeviceId/targetRole/targetHasUdpEndpoint`
  - `stats`：若存在转发计数器则给出（否则为 null）
    - `stats.perSecond.videoPps/videoBps/posePps/poseBps/feedbackPps/feedbackBps`
    - `stats.totals.videoPackets/videoBytes/posePackets/poseBytes/feedbackPackets/feedbackBytes`

---

## 6) 前端跨域与联调建议（Vue3 + Vite）

### 推荐：Vite 代理（避免 CORS）

让前端以同源路径访问（例如 `/api/...`），由 Vite 代理转发：

```ts
// vite.config.ts
export default {
  server: {
    proxy: {
      '/api': {
        target: 'https://localhost:7777',
        changeOrigin: true,
      },
    },
  },
}
```

然后前端直接请求：

- `/api/monitor/udp/stats`
- `/api/monitor/udp/stream`

### SSE 注意点

- SSE 走长连接，浏览器会自动重连。
- 如果你用反向代理（nginx 等），需要确保对 `text/event-stream` 不做缓冲（`proxy_buffering off` 之类）。

---

## 4) 内置调试页（可选）

不提供内置 HTML 页面，前端监管请直接使用 `/api/monitor/udp/stats` 与 `/api/monitor/udp/stream`。
