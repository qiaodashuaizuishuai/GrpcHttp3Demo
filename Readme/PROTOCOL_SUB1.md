# SUB1 协议规范

## 协议概述

**协议名称**：SUB1  
**协议版本**：1.0  
**Magic String**：`SUB1` (ASCII: 0x53 0x55 0x42 0x31)  
**用途**：订阅管理，VR 客户端订阅机器人的数据流  
**QCT0 分类**：Control (Category=3)  
**前置条件**：必须先通过 HELLO 协议完成认证

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
19     2      MsgLen            u16         SUB1 内层负载长度 (Little-Endian)
21     MsgLen Msg               bytes       SUB1 内层负载
```

### 内层：SUB1 负载

```
偏移   长度              字段名                类型        说明
0      4                 Magic                 bytes       固定 ASCII 'S' 'U' 'B' '1'
4      1                 Version               u8          固定 1
5      1                 Flags                 u8          保留字段
6      2                 Reserved              u16         固定 0 (Little-Endian)
8      1                 Operation             u8          1=订阅, 2=取消订阅
9      1                 ResourceMask          u8          资源掩码 (0x01=Pose, 0x02=Video)
10     2                 TokenLen              u16         Token长度 (Little-Endian)
12     2                 DeviceIdLen           u16         发布者设备ID长度 (Little-Endian)
14     TokenLen          Token                 bytes       认证Token (可选)
14+TokenLen  DeviceIdLen  PublisherDeviceId    bytes       发布者设备ID (UTF-8，无终止符)
```

**最小长度**：14字节（不含Token和DeviceId）  
**总长度**：14 + TokenLen + DeviceIdLen

---

## 字段说明

### Operation 枚举

| 值 | 名称 | 说明 |
|---|------|------|
| 1 | Subscribe | 订阅指定发布者的视频流 |
| 2 | Unsubscribe | 取消订阅指定发布者的视频流 |

### ResourceMask 字段

- **类型**：u8 位掩码
- **说明**：指定订阅的资源类型

| 位 | 值 | 名称 | 说明 |
|---|---|------|------|
| 0 | 0x01 | Video | 订阅视频流 |
| 1 | 0x02 | Pose | 订阅姿态数据 |

**示例**：
```
ResourceMask = 0x01  // 只订阅Video
ResourceMask = 0x02  // 只订阅Pose
ResourceMask = 0x03  // 订阅Video和Pose
```

### Token 字段

- **类型**：字节数组
- **长度**：由 TokenLen 字段指定
- **说明**：认证令牌（可选），如果不需要可设置 TokenLen=0

### PublisherDeviceId 字段

- **类型**：UTF-8 字符串（无 null 终止符）
- **长度**：由 DeviceIdLen 字段指定
- **要求**：不能为空
- **填什么**：**填设备ID**，不是连接ID（如 `"robot_test_12bxhjisa"`）

**Q: 订阅时应该发送设备ID还是连接ID？**

**A: 发送设备ID**

示例：
```
PublisherDeviceId = "robot_test_12bxhjisa"  // ✅ 填设备ID
PublisherDeviceId = "client_xxx..."         // ❌ 不要填连接ID
```

服务器会自动从你的连接上下文中获取你的连接ID（`context.ConnectionId`），客户端不需要关心。

---

## 消息流程

### 订阅流程 (Operation=1)

```
VR Client                             Server
  │                                      │
  │  1. 发送 SUB1 消息                   │
  │     Version = 1                      │
  │     Flags = 0                        │
  │     Operation = 1                    │
  │     ResourceMask = 0x01 (Video)      │
  │     TokenLen = 12                    │
  │     DeviceIdLen = 20                 │
  │     Token = "secret_token"           │
  │     PublisherDeviceId = "robot_test_12bxhjisa" │
  │ ──────────────────────────────────> │
  │                                      │
  │                                      │  2. 服务器处理
  │                                      │     - AddSubscription()
  │                                      │
  │  3. 接收 SUB_ACK 响应                │
  │     Payload: "SUB_ACK|robot_test_12bxhjisa" │
  │ <────────────────────────────────── │
  │                                      │
```

### 取消订阅流程 (Operation=2)

```
VR Client                             Server
  │                                      │
  │  1. 发送 SUB1 消息                   │
  │     Version = 1                      │
  │     Operation = 2                    │
  │     ResourceMask = 0x01              │
  │     PublisherDeviceId = "robot_test_12bxhjisa" │
  │ ──────────────────────────────────> │
  │                                      │
  │                                      │  2. 服务器处理
  │                                      │     - RemoveSubscription()
  │                                      │
  │  3. 接收 UNSUB_ACK 响应              │
  │     Payload: "UNSUB_ACK|robot_test_12bxhjisa" │
  │ <────────────────────────────────── │
  │                                      │
```

---

## 服务器处理流程

### 订阅处理 (Operation=1)

```
1. 验证 DeviceId 已认证
   ↓
2. 验证 PublisherDeviceId 不为空
   ↓
3. 调用 SubscriptionManager.AddSubscription()
   - publisherDeviceId
   - subscriberConnId
   ↓
4. 返回 "SUB_ACK|{publisherDeviceId}"
```

### 取消订阅处理 (Operation=2)

```
1. 验证 DeviceId 已认证
   ↓
2. 验证 PublisherDeviceId 不为空
   ↓
3. 调用 SubscriptionManager.RemoveSubscription()
   - publisherDeviceId
   - subscriberConnId
   ↓
4. 返回 "UNSUB_ACK|{publisherDeviceId}"
```

---

## 订阅关系管理

### 内部映射机制（服务器端）

当VR客户端发送SUB1订阅消息时：

```
VR Client → Server: SUB1
  Version = 1
  Flags = 0
  Operation = 1
  ResourceMask = 0x01 (Video)
  TokenLen = 12
  DeviceIdLen = 20
  Token = "secret_token"
  PublisherDeviceId = "robot_test_12bxhjisa"  ← 客户端发送设备ID
  ↓
Server 自动处理:
  subscriberConnId = context.ConnectionId  (如 "client_882c90ef0c55488a93c4bb6c875fee2e")
  ↓
Server: AddSubscription(
  publisherDeviceId: "robot_test_12bxhjisa",     // 客户端提供的设备ID
  subscriberConnId: "client_882c90ef0c55488a...", // 服务器自动获取的连接ID
  resourceMask: 0x01
)
```

### SubscriptionManager 内部结构

```
_subscriptions (正向映射: 设备ID → 连接ID列表):
  "robot_test_12bxhjisa" -> ["client_882c90ef...", "client_xyz..."]
  "robot_002" -> ["client_882c90ef..."]

_reverseSubscriptions (反向映射: 连接ID → 设备ID列表):
  "client_882c90ef..." -> ["robot_test_12bxhjisa", "robot_002"]
  "client_xyz..." -> ["robot_test_12bxhjisa"]
```

**关键点**：
- **客户端**：只需要知道设备ID（如"robot_001"）
- **服务器**：自动管理连接ID映射
- **转发时**：服务器根据设备ID查询订阅的连接ID列表，然后转发

### 连接断开时的清理

```
订阅者断开:
  RemoveAllSubscriptionsForConnection(subscriberConnId)
  - 清理正向映射
  - 清理反向映射
```

---

## 错误处理

### 未认证

**响应**：
```
Code: Unauthorized (5001)
Payload: "Device not authorized"
```

### PublisherDeviceId 为空

**响应**：
```
Code: InvalidFormat (1002)
Payload: "Invalid publisher device ID"
```

---

## 文档版本

- **版本**：1.0
- **日期**：2025-10-23
- **状态**：已实现
