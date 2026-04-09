# PAR1 协议规范

## 协议概述

**协议名称**：PAR1  
**协议版本**：1.0  
**Magic String**：`PAR1` (ASCII: 0x50 0x41 0x52 0x31)  
**用途**：设备配对管理  
**QCT0 分类**：Control (Category=3)  
**前置条件**：设备必须先通过 HELLO 协议完成认证

---

## 消息格式

### 外层：QCT0 封装

```
偏移   长度   字段名            类型        说明
0      4      Magic             bytes       固定 ASCII 'Q' 'C' 'T' '0'
4      1      Version           u8          固定 0
5      1      Flags             u8          ControlKind: 1=Info
6      2      Reserved          u16         固定 0
8      8      TimestampMs       u64         Unix 毫秒时间戳 (Little-Endian)
16     2      Code              u16         AppErrorCode (Little-Endian)
18     1      Category          u8          AppCategory: 3=Control
19     2      MsgLen            u16         PAR1 内层负载长度 (Little-Endian)
21     MsgLen Msg               bytes       PAR1 内层负载
```

### 内层：PAR1 负载

```
偏移   长度         字段名           类型    说明
0      4            Magic            bytes   固定 ASCII 'P' 'A' 'R' '1'
4      1            Version          u8      协议版本，当前为 1
5      1            Flags            u8      标志位
6      2            Reserved         u16     保留字段 (Little-Endian)
8      1            Operation        u8      操作类型
9      1            WorkState        u8      工作状态
10     2            PeerIdLen        u16     对端设备ID长度 (Little-Endian)
12     PeerIdLen    PeerId           bytes   对端设备ID (UTF-8)
12+N   2            PeerConnIdLen    u16     对端连接ID长度 (Little-Endian)
14+N   PeerConnIdLen PeerConnId      bytes   对端连接ID (UTF-8，可选)
```

---

## 字段说明

### Operation 枚举

| 值 | 名称 | 说明 |
|---|------|------|
| 1 | Pair | 配对操作 |
| 2 | Unpair | 取消配对操作 |

### Flags 枚举（位标志）

| 值 | 名称 | 说明 |
|---|------|------|
| 0x00 | Request/Notify | 请求或通知 |
| 0x01 | ACK | 仅确认（用于拒绝配对时的响应） |
| 0x02 | ACCEPT | 接受标志位 |
| 0x03 | ACK\|ACCEPT | 确认并接受（0x01 \| 0x02，用于同意配对） |

**说明**：
- **机器人拒绝配对**：Flags = `0x01` (ACK only)，表示收到但拒绝
- **机器人接受配对**：Flags = `0x03` (ACK | ACCEPT)，表示收到并同意
- **服务器响应VR请求**：Flags = `0x01` (ACK)，表示请求已收到待机器人响应

**Flags 使用场景汇总**：

| 消息方向 | Operation | Flags | 含义 |
|---------|-----------|-------|------|
| VR → Server | 1 (Pair) | 0x00 | VR发起配对请求 |
| Server → VR | 1 (Pair) | 0x01 | 服务器确认收到请求 |
| Server → Robot | 1 (Pair) | 0x00 | 服务器通知机器人有配对请求 |
| Robot → Server | 1 (Pair) | **0x01** | **机器人拒绝配对** |
| Robot → Server | 1 (Pair) | **0x03** | **机器人接受配对** |
| Server → VR | 1 (Pair) | 0x03 | 服务器通知VR配对成功 |
| Server → VR | 1 (Pair) | 0x01 (Code=PairRejected) | 服务器通知VR被拒绝 |
| Any → Server | 2 (Unpair) | 0x00 | 发起取消配对 |
| Server → Peer | 2 (Unpair) | 0x00 | 通知对端取消配对 |

**机器人端如何区分消息类型**：

机器人会收到来自服务器的两类PAR1消息（Op=1），通过**Flags**字段区分：

| Flags | 消息类型 | 机器人应做的事 |
|-------|---------|--------------|
| **0x00** | **配对请求通知** | 需要决策：发送0x03接受 或 0x01拒绝 |
| **0x03** | **配对成功确认** | 最终确认，配对已完成，准备接收START指令 |

**示例流程**：
```
1. Server → Robot: Flags=0x00, PeerId="vr_abc"
   → 机器人判断：这是配对请求，需要我决定接受或拒绝

2. Robot → Server: Flags=0x03, PeerId="vr_abc"
   → 机器人回复：我接受配对

3. Server → Robot: Flags=0x03, PeerId="vr_abc"
   → 机器人判断：这是最终确认，配对完成
```

### WorkState 枚举

| 值 | 名称 | 说明 |
|---|------|------|
| 0 | Unknown | 未知状态 |
| 1 | Ready | 就绪/待机 |
| 2 | Working | 工作中 |
| 3 | Paused | 暂停 |
| 4 | Error | 错误 |

### PeerId 字段

- **类型**：UTF-8 字符串（无 null 终止符）
- **长度**：PeerIdLen 指定
- **用途**：要配对/取消配对的对端设备 ID

### PeerConnId 字段

- **类型**：UTF-8 字符串（无 null 终止符）
- **长度**：PeerConnIdLen 指定
- **用途**：对端连接 ID（用于通知时指定目标连接）

---

## 配对流程 (Operation=1)

### 步骤 1-3：VR 发起配对请求

```
VR Client                             Server                           Robot
  │                                      │                                │
  │  1. 发送 PAR1                        │                                │
  │     Op=1, Flags=0x00                 │                                │
  │     PeerId="robot_001"               │                                │
  │ ──────────────────────────────────> │                                │
  │                                      │                                │
  │                                      │  2. 服务器处理                 │
  │                                      │     - RegisterPendingPair()    │
  │                                      │     - 查找机器人连接            │
  │                                      │                                │
  │  3. 接收 ACK                         │                                │
  │     Code=Ok, Payload="PAIR_ACK"      │                                │
  │ <────────────────────────────────── │                                │
  │                                      │                                │
```

### 步骤 4：服务器通知机器人（配对请求）

```
VR Client                             Server                           Robot
  │                                      │                                │
  │                                      │  4. 通知机器人 (配对请求)      │
  │                                      │     Op=1, Flags=0x00           │
  │                                      │     PeerId="vr_abc"            │
  │                                      │     PeerConnId="conn_123"      │
  │                                      │ ─────────────────────────────> │
  │                                      │                                │
  │                                      │                      机器人收到后判断：
  │                                      │                      - Flags=0x00 → 配对请求，需决策
  │                                      │                      - 可接受(0x03)或拒绝(0x01)
```

### 步骤 5-7：机器人接受配对

```
VR Client                             Server                           Robot
  │                                      │                                │
  │                                      │  5. 机器人发送接受             │
  │                                      │     Op=1, Flags=0x03 (ACK|ACCEPT) │
  │                                      │     PeerId="vr_abc"            │
  │                                      │     WorkState=Working          │
  │                                      │ <───────────────────────────── │
  │                                      │                                │
  │                                      │  6. 服务器处理                 │
  │                                      │     - UpdatePairing() 双向     │
  │                                      │     - ClearPendingPair()       │
  │                                      │                                │
  │  7a. 通知 VR 配对成功                │                                │
  │     Op=1, Flags=0x03 (ACK|ACCEPT)    │                                │
  │     PeerId="robot_001"               │                                │
  │     WorkState=Working                │                                │
  │ <────────────────────────────────── │                                │
  │                                      │                                │
  │                                      │  7b. 回复机器人 (最终确认)     │
  │                                      │     Op=1, Flags=0x03 (ACK|ACCEPT) │
  │                                      │     PeerId="vr_abc"            │
  │                                      │     PeerConnId="conn_123"      │
  │                                      │ ─────────────────────────────> │
  │                                      │                                │
  │                                      │                      机器人收到后判断：
  │                                      │                      - Flags=0x03 → 最终确认，配对完成
  │                                      │                                │
  │                                      │  8. 下发 START 指令            │
  │                                      │ ─────────────────────────────> │
  │                                      │                                │
```

### 步骤 5'-7'：机器人拒绝配对（可选分支）

```
VR Client                             Server                           Robot
  │                                      │                                │
  │                                      │  5'. 机器人发送拒绝            │
  │                                      │     Op=1, Flags=0x01 (ACK only) │
  │                                      │     PeerId="vr_abc"            │
  │                                      │ <───────────────────────────── │
  │                                      │                                │
  │                                      │  6'. 服务器处理                │
  │                                      │     - ClearPendingPair()       │
  │                                      │                                │
  │  7'. 通知 VR 配对被拒绝              │                                │
  │     Code=PairRejected                │                                │
  │     Flags=0x01 (ACK only)            │                                │
  │ <────────────────────────────────── │                                │
  │                                      │                                │
```

---

## 取消配对流程 (Operation=2)

```
Initiator                             Server                           Peer
  │                                      │                                │
  │  1. 发送 PAR1                        │                                │
  │     Op=2, Flags=0x00                 │                                │
  │     PeerId="peer_device"             │                                │
  │ ──────────────────────────────────> │                                │
  │                                      │                                │
  │                                      │  2. 服务器处理                 │
  │                                      │     - UpdatePairing(false) 双向│
  │                                      │                                │
  │  3. 接收 ACK                         │                                │
  │     Code=Ok, Payload="UNPAIR_ACK"    │                                │
  │ <────────────────────────────────── │                                │
  │                                      │                                │
  │                                      │  4. 通知对端                   │
  │                                      │     Op=2, Flags=0x00           │
  │                                      │     PeerId="initiator_device"  │
  │                                      │ ─────────────────────────────> │
  │                                      │                                │
```

---

## 服务器处理流程

### 配对请求 (Op=1, Flags=0x00)

```
1. 提取发起者信息
   myDeviceId = context.DeviceId
   myConnId = context.ConnectionId
   peerId = message.PeerId
   ↓
2. 区分角色（VR 或 Robot）
   IF myDeviceId 是 VR THEN
       发起者 = VR
       RegisterPendingPair(peerId=robot, vrDeviceId, vrConnId)
       通知机器人
   ELSE IF myDeviceId 是 Robot THEN
       发起者 = Robot（通知）
       仅记录日志
   ↓
3. 返回 "PAIR_ACK"
```

### 配对接受 (Op=1, Flags=0x03)

```
1. 提取信息
   robotDeviceId = context.DeviceId
   vrDeviceId = message.PeerId
   workState = message.WorkState
   ↓
2. 验证Flags
   IF Flags != 0x03 (ACK | ACCEPT) THEN
       返回错误
   ↓
3. 更新双向配对状态
   UpdatePairing(robotDeviceId, true, vrDeviceId, workState)
   UpdatePairing(vrDeviceId, true, robotDeviceId, WorkState.Working)
   ↓
4. 清理待定记录
   ClearPendingPair(robotDeviceId)
   ↓
5. 通知 VR 配对成功
   发送 PAR1 (Op=1, Flags=0x03) 到 VR
```

### 配对拒绝 (Op=1, Flags=0x01)

```
1. 验证Flags
   IF Flags != 0x01 (ACK only, 无ACCEPT位) THEN
       返回错误
   ↓
2. 清理待定记录
   ClearPendingPair(robotDeviceId)
   ↓
3. 通知 VR 配对被拒绝
   返回 Code=PairRejected, Flags=0x01
```

### 取消配对 (Op=2, Flags=0x00)

```
1. 提取信息
   myDeviceId = context.DeviceId
   peerId = message.PeerId
   ↓
2. 更新双向状态
   UpdatePairing(myDeviceId, false, null, WorkState.Ready)
   UpdatePairing(peerId, false, null, WorkState.Ready)
   ↓
3. 通知对端
   发送 PAR1 (Op=2, Flags=0x00) 到对端
   ↓
4. 返回 "UNPAIR_ACK"
```

---

## 错误处理

### 对端设备离线

**场景**：VR 请求配对，但机器人不在线

**响应**：
```
Code: PeerLost (2003)
Payload: "Peer device not found or offline"
```

### 对端拒绝配对

**场景**：机器人发送 Flags=0x01

**响应给 VR**：
```
Code: PairRejected (2004)
Payload: "Pairing request rejected by peer"
```

### 未认证

**响应**：
```
Code: Unauthorized (5001)
Payload: "Device not authorized"
```

---

## 文档版本

- **版本**：1.0
- **日期**：2025-10-23
- **状态**：已实现
