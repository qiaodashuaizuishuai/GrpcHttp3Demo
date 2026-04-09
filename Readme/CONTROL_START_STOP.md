# START/STOP 控制消息规范

## 协议概述

**用途**：控制设备启动/停止数据发送（视频流/位姿数据）  
**封装协议**：QCT0  
**消息格式**：纯文本（UTF-8）  
**触发时机**：配对成功后自动下发，或手动控制

---

## 消息格式

### START 消息

```
偏移   长度   字段名            类型        说明
0      4      Magic             bytes       固定 ASCII 'Q' 'C' 'T' '0'
4      1      Version           u8          固定 0
5      1      Flags             u8          ControlKind.Info (1)
6      2      Reserved          u16         固定 0
8      8      TimestampMs       u64         Unix 毫秒时间戳 (Little-Endian)
16     2      Code              u16         AppErrorCode.Ok (0)
18     1      Category          u8          AppCategory (Video=1 或 Pose=2)
19     2      MsgLen            u16         5 (固定)
21     5      Msg               bytes       ASCII "start" (UTF-8)
```

### STOP 消息

```
偏移   长度   字段名            类型        说明
0      4      Magic             bytes       固定 ASCII 'Q' 'C' 'T' '0'
4      1      Version           u8          固定 0
5      1      Flags             u8          ControlKind.Info (1)
6      2      Reserved          u16         固定 0
8      8      TimestampMs       u64         Unix 毫秒时间戳 (Little-Endian)
16     2      Code              u16         AppErrorCode.Ok (0)
18     1      Category          u8          AppCategory (Video=1 或 Pose=2)
19     2      MsgLen            u16         4 (固定)
21     4      Msg               bytes       ASCII "stop" (UTF-8)
```

---

## 字段说明

### Category 分类

| 值 | 名称 | 控制对象 |
|---|------|---------|
| 1 | Video | 视频流发送 |
| 2 | Pose | 位姿数据发送 |

### 消息内容（Msg）

- **"start"** (不区分大小写)：启动数据发送
- **"stop"** (不区分大小写)：停止数据发送

---

## 使用场景

### 1. 配对成功后自动下发

配对成功后，服务器自动向双方设备下发START指令：

```
Server → VR Client:
  Category = Pose (2)
  Msg = "start"
  → VR开始发送位姿数据

Server → Robot:
  Category = Video (1)
  Msg = "start"
  → Robot开始发送视频流
```

### 2. 手动控制

客户端/服务器可随时下发START/STOP控制数据流：

```
停止视频：
  Category = Video (1)
  Msg = "stop"

恢复视频：
  Category = Video (1)
  Msg = "start"
```

---

## 服务器处理逻辑

### START 消息

```
1. 解析 QCT0 消息
   Category = Video/Pose
   Msg = "start"
   ↓
2. 获取设备 DeviceId
   ↓
3. 更新发布者状态
   subscriptionManager.UpdatePublisherVideo(deviceId, v => v.IsSending = true)
   ↓
4. 设备开始发送数据
```

### STOP 消息

```
1. 解析 QCT0 消息
   Category = Video/Pose
   Msg = "stop"
   ↓
2. 获取设备 DeviceId
   ↓
3. 更新发布者状态
   subscriptionManager.UpdatePublisherVideo(deviceId, v => v.IsSending = false)
   ↓
4. 设备停止发送数据
```

---

## 客户端实现

### 接收 START 消息

```csharp
void HandleQct0Control(Qct0Message msg)
{
    if (msg.Category == AppCategory.Video && 
        msg.Msg == "start")
    {
        // 启动视频编码和发送
        StartVideoCapture();
    }
}
```

### 接收 STOP 消息

```csharp
void HandleQct0Control(Qct0Message msg)
{
    if (msg.Category == AppCategory.Video && 
        msg.Msg == "stop")
    {
        // 停止视频编码和发送
        StopVideoCapture();
    }
}
```

---

## 与其他协议的关系

| 协议 | 作用 | START/STOP 的角色 |
|------|------|------------------|
| PAR1 | 设备配对 | 配对成功后触发START下发 |
| VPAR | 视频参数配置 | START后机器人立即发送VPAR |
| QVR0 | 关键帧传输 | START后开始传输视频流 |
| Datagram | 非关键帧/位姿 | START后开始传输实时数据 |

**典型流程**：
```
1. PAR1配对成功
   ↓
2. Server下发START (Video)
   ↓
3. Robot发送VPAR (视频参数)
   ↓
4. Robot开始发送QVR0和Datagram视频流
```

---

**最后更新**: 2025年11月5日  
**消息长度**: 26字节 (START) / 25字节 (STOP)  
**字节序**: Little-Endian  
**字符集**: UTF-8
