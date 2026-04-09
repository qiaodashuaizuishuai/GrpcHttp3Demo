using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Core.Interfaces;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        /// <summary>
        /// 绑定事件发送器（Push 通道：WS 或 gRPC streaming）到会话
        /// </summary>
        public void AttachEventSender(string sessionId, IEventStreamSender sender)
        {
            if (_sessions.TryGetValue(sessionId, out var ctx))
            {
                ctx.EventSender = sender;
            }
        }

        /// <summary>
        /// 解除事件发送器（Push 通道）绑定。
        /// 注意：Push 通道断开不等于客户端离线，这里只表示“无法推送信令”。
        /// </summary>
        public void DetachEventSender(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var ctx))
            {
                ctx.EventSender = null;
            }
        }

        /// <summary>
        /// 解除事件发送器（Push 通道）绑定，但仅当当前绑定的 sender 与传入实例一致。
        /// 用于处理“旧连接后断开”覆盖“新连接已重绑”的竞态。
        /// </summary>
        public void DetachEventSender(string sessionId, IEventStreamSender sender)
        {
            if (_sessions.TryGetValue(sessionId, out var ctx) && ReferenceEquals(ctx.EventSender, sender))
            {
                ctx.EventSender = null;
            }
        }

        /// <summary>
        /// 检查推送通道是否已绑定（WS 或 gRPC streaming）
        /// </summary>
        public bool IsPushConnected(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var ctx) && ctx.EventSender != null;
        }

        /// <summary>
        /// 异步发送事件消息（带重试机制）
        /// </summary>
        public async Task SendEventAsync(string targetSessionId, EventMessage message)
        {
            if (_sessions.TryGetValue(targetSessionId, out var ctx) && ctx.EventSender != null)
            {
                // 启动后台重试任务，不阻塞当前调用
                _ = SendWithRetryAsync(ctx.EventSender, message, targetSessionId);
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// 带重试策略的发送逻辑
        /// </summary>
        private async Task SendWithRetryAsync(IEventStreamSender sender, EventMessage message, string targetSessionId)
        {
            // 策略：0s, 10s, 60s
            int[] delays = { 0, 10, 60 };

            foreach (var delaySeconds in delays)
            {
                if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                try
                {
                    await sender.WriteAsync(message);
                    return; // 发送成功，退出
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ConnMgr] Failed to send event to {targetSessionId} (Retry {delaySeconds}s): {ex.Message}");
                    // 继续下一次重试
                }
            }
            Console.WriteLine($"[ConnMgr] Give up sending event to {targetSessionId} after retries. Detaching push sender.");

            // 推送持续失败通常意味着底层连接已断开/不可用。
            // 这里不要影响会话在线判定（在线仍以心跳为准），只标记 push 通道不可用。
            DetachEventSender(targetSessionId, sender);
        }

        /// <summary>
        /// 更新心跳时间
        /// </summary>
        public void UpdateHeartbeat(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var ctx))
            {
                ctx.LastHeartbeatUtc = DateTime.UtcNow;
                ctx.GrpcRescueCount = 0; // 重置救援计数
            }
        }

        /// <summary>
        /// 检查并救援失联会话
        /// </summary>
        /// <param name="timeout">超时阈值</param>
        public void CheckAndRescueSessions(TimeSpan timeout)
        {
            var now = DateTime.UtcNow;
            var lostSessions = new List<string>();

            foreach (var kv in _sessions)
            {
                var ctx = kv.Value;
                if (now - ctx.LastHeartbeatUtc > timeout)
                {
                    // 场景 B: 心跳失联（客户端可能网络波动/切网/线程卡死）
                    if (IsPushConnected(kv.Key))
                    {
                        if (ctx.GrpcRescueCount < 3)
                        {
                            ctx.GrpcRescueCount++;
                            Console.WriteLine($"[ConnMgr] Rescuing session {kv.Key} (Attempt {ctx.GrpcRescueCount}) via push channel...");
                            
                            var cmd = new EventMessage
                            {
                                TargetSessionId = kv.Key,
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                System = new SystemCommand { Action = SystemCommand.Types.Action.RequestPing }
                            };
                            // 发送救援指令：如果 push 已不可用，SendWithRetry 会自动 detach。
                            _ = SendWithRetryAsync(ctx.EventSender!, cmd, kv.Key);
                        }
                        else
                        {
                            // 救援失败，放弃
                            Console.WriteLine($"[ConnMgr] Rescue failed for {kv.Key}. Cleaning up.");
                            lostSessions.Add(kv.Key);
                        }
                    }
                    else
                    {
                        // 场景 C: 全部失联
                        lostSessions.Add(kv.Key);
                    }
                }
            }

            foreach (var sessionId in lostSessions)
            {
                Console.WriteLine($"[ConnMgr] Session timed out: {sessionId}");
                UnregisterGrpc(sessionId);
            }
        }
    }
}
