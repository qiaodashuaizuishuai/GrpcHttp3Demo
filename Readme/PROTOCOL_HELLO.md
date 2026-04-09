# HELLO 协议规范

## 协议概述

**协议名称**：HELLO  
**协议版本**：1.0  
**Magic String**：`HLLO` (ASCII: 0x48 0x4C 0x4C 0x4F)  
**用途**：设备认证和身份识别  
**QCT0 分类**：Control (Category=3)  

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
19     2      MsgLen            u16         HELLO 内层负载长度 (Little-Endian)
21     MsgLen Msg               bytes       HELLO 内层负载
```

### 内层：HELLO 负载

```
偏移   长度         字段名           类型        说明
0      4            Magic            bytes       固定 ASCII 'H' 'L' 'L' 'O'
4      1            Version          u8          协议版本，当前为 1
5      1            Flags            u8          保留标志位
6      2            Reserved         u16         保留字段 (Little-Endian)
8      2            TokenLen         u16         Token 长度 (Little-Endian)
10     2            DeviceIdLen      u16         DeviceId 长度 (Little-Endian)
12     1            EndpointType     u8          端点类型
13     TokenLen     Token            bytes       认证令牌（可选）
13+N   DeviceIdLen  DeviceId         bytes       设备 ID (UTF-8)
```

---

## 字段说明

### EndpointType 枚举

| 值 | 名称 | 说明 |
|---|------|------|
| 0 | Unknown | 未知类型 |
| 1 | Robot | 机器人设备 |
| 2 | VrClient | VR 客户端 |

### Token 字段

- **类型**：任意字节序列
- **长度**：TokenLen 指定
- **用途**：预留用于未来的身份认证

### DeviceId 字段

- **类型**：UTF-8 字符串（无 null 终止符）
- **长度**：DeviceIdLen 指定
- **要求**：不能为空

---

## 消息流程

```
Client                                Server
  │                                      │
  │  1. 打开单向流                       │
  │ ──────────────────────────────────> │
  │                                      │
  │  2. 发送 HELLO 消息                  │
  │     (QCT0 封装)                      │
  │ ──────────────────────────────────> │
  │                                      │
  │  3. 关闭流 (FIN)                     │
  │ ──────────────────────────────────> │
  │                                      │
  │                                      │  4. 服务器处理
  │                                      │     - 解析 HELLO
  │                                      │     - 验证 DeviceId
  │                                      │     - 注册到 ActiveSessions
  │                                      │
  │  5. 接收 HELLO_ACK 响应              │
  │     (新的单向流)                     │
  │ <────────────────────────────────── │
  │                                      │
  │  6. 认证完成                         │
  │                                      │
```

---

## 服务器处理流程

```
1. 解析 HELLO 消息
   ↓
2. 验证 DeviceId 不为空
   ↓
3. 调用 ActiveSessions.RegisterOrUpdate()
   - 存储 DeviceId
   - 存储 EndpointType
   - 更新连接状态
   ↓
4. 构造 HELLO_ACK 响应
   ↓
5. 返回 ProtocolResult.Ok()
```

---

## 错误处理

### DeviceId 为空

**响应**：
```
Code: InvalidFormat (1002)
Payload: "Device ID is required"
```

### Magic 不匹配

**行为**：
- 记录警告日志
- 不发送响应
- 连接保持打开状态

---

## 文档版本

- **版本**：1.0
- **日期**：2025-10-23
- **状态**：已实现
