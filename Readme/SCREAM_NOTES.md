# SCReAM 集成备忘（服务器侧转发场景）

当前服务器已去掉 SCReAM 状态，以下记录如何基于现有包格式恢复/接入 SCReAM，供端侧或未来改造参考。

## 视频包头（机器人上行 -> 服务器）
- Byte 0: `0x01`（视频魔数）
- Bytes 1-2: `SeqNr` (uint16，循环)
- Bytes 3-10: `Timestamp_ntp64` (uint64，发送端 NTP 时间)
- Payload: 视频数据
- SSRC: 线上未带。若要跑 SCReAM，建议显式增加 SSRC 字段；临时可用 sessionId hash 推导，但不精确。

## 反馈包（服务器/VR -> 机器人）——建议格式
- Byte 0: `0x03`（反馈魔数）
- Bytes 1-4: `SSRC` (uint32) — 对应视频流的 SSRC
- Bytes 5-6: `HighestSeqNr` (uint16) — 最高收到的序号
- Bytes 7-14: `AckVector` (uint64) — bit i 表示 `HighestSeqNr - i` 是否收到（类似 RFC8888）
- Bytes 15-18: `Timestamp_ntp` (uint32) — 反馈生成时的本地 NTP 时间
- Bytes 19-22: `RecommendedBitrateKbps` (float32) — 可选码率上限，若不用填 0

## 若在服务器侧恢复 SCReAM Rx（上行）
1) 每个视频包解析 `SeqNr`、`Timestamp_ntp64`（取中间 32bit 给 SCReAM），建立 session -> SSRC 映射。
2) 调用 `ScreamRx.Receive(now_ntp, ssrc, size, seqNr, ecnCe=false, senderNtp32)` 更新状态。
3) 当 `ShouldSendFeedback` 为真时，按上述反馈格式填充并发给机器人（配对 session 的 UDP 端口）。

## 若在服务器侧使用 SCReAM Tx（下行，服务器向 VR 发送媒体时）
- 每条下行流维持一个 `ScreamTx`。
- 发送时调用 `NewMediaFrame` / `IsOkToTransmit` / `AddTransmitted` 记录 seq/size/time。
- 收到 VR 反馈（0x03）按上面格式解析后，调用 `IncomingFeedback(time_ntp, buffer)`。
- 用 `GetTargetBitrate` 驱动编码/节流。

## 当前最小化策略（已启用）
- 服务器只做透明转发：0x03 反馈从 VR 直接转发到配对机器人，不做任何码率计算或状态缓存。

## 建议（若以后重启 SCReAM）
- 在视频头里增加显式 SSRC，避免依赖 sessionId hash。
- 加抖动阈值：码率变动超过 20% 且间隔 > 300ms 再更新，避免抖动。
- 反馈频率上限：高码率时 ≤10ms，一般可放宽。
- 缺路由时静默丢弃反馈，不修改媒体负载。
