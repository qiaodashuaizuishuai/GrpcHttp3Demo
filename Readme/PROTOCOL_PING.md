# PING 协议规范

## 协议概述

**协议名称**：PING  
**协议版本**：1.0  
**Magic String**：`PING` (ASCII: 0x50 0x49 0x4E 0x47)  
**用途**：心跳检测  
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
19     2      MsgLen            u16         PING 内层负载长度 (Little-Endian)
21     MsgLen Msg               bytes       PING 内层负载
```

### 内层：PING 负载

```
偏移   长度   字段名   类型    说明
0      4      Magic    bytes   固定 ASCII 'P' 'I' 'N' 'G'
```

**PING 总长度**：4 字节

### PONG 响应

服务器接收到 PING 后返回包含 "PONG" 文本的响应：

```
QCT0:
  Code = Ok (0)
  Category = Control (3)
  Flags = Info (1)
  Payload = UTF-8 "PONG"
```

---

## 消息流程

```
Client                                Server
  │                                      │
  │  1. 发送 PING 消息                   │
  │     Magic = "PING"                   │
  │ ──────────────────────────────────> │
  │                                      │
  │                                      │  2. 服务器处理
  │                                      │     - 验证 Magic
  │                                      │     - 构造 PONG
  │                                      │
  │  3. 接收 PONG 响应                   │
  │     Payload = "PONG"                 │
  │ <────────────────────────────────── │
  │                                      │
```

---

## 服务器处理流程

```
1. 验证 Magic = "PING"
   ↓
2. 构造 QCT0 响应
   - Code = Ok
   - Payload = "PONG"
   ↓
3. 返回 ProtocolResult.Ok()
```

---

## 错误处理

### Magic 不匹配

**行为**：
- 返回解析失败
- 不发送响应

---

## 文档版本

- **版本**：1.0
- **日期**：2025-10-23
- **状态**：已实现
