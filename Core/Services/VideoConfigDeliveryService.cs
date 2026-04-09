using System.Collections.Concurrent;
using GrpcHttp3Demo.Protos;
using Microsoft.Extensions.Logging;

namespace GrpcHttp3Demo.Core.Services
{
    /// <summary>
    /// 负责 VideoConfig 的可靠投递（ACK + 重试）
    /// </summary>
    public class VideoConfigDeliveryService
    {
        private readonly ILogger<VideoConfigDeliveryService> _logger;
        // Key: ConfigID, Value: TaskCompletionSource for ACK
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();

        public VideoConfigDeliveryService(ILogger<VideoConfigDeliveryService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 收到 ACK 时调用
        /// </summary>
        public void CompleteAck(string configId, bool success)
        {
            if (_pendingAcks.TryGetValue(configId, out var tcs))
            {
                tcs.TrySetResult(success);
            }
        }

        /// <summary>
        /// 执行可靠发送流程
        /// </summary>
        public async Task SendReliablyAsync(
            string configId, 
            string targetSessionId,
            Func<Task> sendAction, 
            Func<Task> disconnectAction)
        {
            int maxRetries = 5;
            TimeSpan timeout = TimeSpan.FromSeconds(3);

            for (int i = 0; i <= maxRetries; i++)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // 注册 TCS，覆盖旧的（如果是重试）
                _pendingAcks[configId] = tcs;

                try
                {
                    _logger.LogInformation($"[VideoConfig] Sending to {targetSessionId}, Attempt {i + 1}/{maxRetries + 1}, ConfigId={configId}");
                    
                    // 执行发送
                    await sendAction();

                    // 等待 ACK 或 超时
                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
                    
                    if (completedTask == tcs.Task)
                    {
                        // 收到结果
                        _pendingAcks.TryRemove(configId, out _); // 清理

                        if (tcs.Task.Result)
                        {
                            _logger.LogInformation($"[VideoConfig] ACK received from {targetSessionId} for {configId}");
                            return; // 成功，结束
                        }
                        else 
                        {
                            _logger.LogWarning($"[VideoConfig] NACK received from {targetSessionId} for {configId}. Treating as retry-able failure.");
                            // NACK 也视为失败，继续重试
                        }
                    }
                    else
                    {
                        // 超时
                        _logger.LogWarning($"[VideoConfig] Timeout waiting for ACK from {targetSessionId} (Attempt {i + 1})");
                        // 继续下一轮循环
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[VideoConfig] Exception delivering to {targetSessionId}");
                }
            }

            // 循环结束仍未成功
            _logger.LogError($"[VideoConfig] Failed to deliver to {targetSessionId} after {maxRetries + 1} attempts. Disconnecting session.");
            
            // 确保清理
            _pendingAcks.TryRemove(configId, out _);
            
            // 触发断开连接
            try
            {
                await disconnectAction();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing disconnect action");
            }
        }
    }
}
