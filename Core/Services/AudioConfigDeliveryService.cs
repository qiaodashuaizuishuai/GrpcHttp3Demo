using System.Collections.Concurrent;
using GrpcHttp3Demo.Protos;
using Microsoft.Extensions.Logging;

namespace GrpcHttp3Demo.Core.Services
{
    /// <summary>
    /// 负责 AudioConfig 的可靠投递（ACK + 重试）
    /// </summary>
    public class AudioConfigDeliveryService
    {
        private readonly ILogger<AudioConfigDeliveryService> _logger;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();

        public AudioConfigDeliveryService(ILogger<AudioConfigDeliveryService> logger)
        {
            _logger = logger;
        }

        public void CompleteAck(string configId, bool success)
        {
            if (_pendingAcks.TryGetValue(configId, out var tcs))
            {
                tcs.TrySetResult(success);
            }
        }

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
                _pendingAcks[configId] = tcs;

                try
                {
                    _logger.LogInformation($"[AudioConfig] Sending to {targetSessionId}, Attempt {i + 1}/{maxRetries + 1}, ConfigId={configId}");

                    await sendAction();

                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
                    if (completedTask == tcs.Task)
                    {
                        _pendingAcks.TryRemove(configId, out _);

                        if (tcs.Task.Result)
                        {
                            _logger.LogInformation($"[AudioConfig] ACK received from {targetSessionId} for {configId}");
                            return;
                        }

                        _logger.LogWarning($"[AudioConfig] NACK received from {targetSessionId} for {configId}. Treating as retry-able failure.");
                    }
                    else
                    {
                        _logger.LogWarning($"[AudioConfig] Timeout waiting for ACK from {targetSessionId} (Attempt {i + 1})");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[AudioConfig] Exception delivering to {targetSessionId}");
                }
            }

            _logger.LogError($"[AudioConfig] Failed to deliver to {targetSessionId} after {maxRetries + 1} attempts. Disconnecting session.");
            _pendingAcks.TryRemove(configId, out _);

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