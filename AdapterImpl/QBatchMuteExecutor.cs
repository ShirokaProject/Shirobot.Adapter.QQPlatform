using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal static class QBatchMuteExecutor
{
    internal static async Task<QBatchOperationResult> ExecuteAsync(IReadOnlyList<QMemberMute> members,
        Func<QMemberMute, CancellationToken, Task> execute, Func<Exception, bool> isDefinitiveFailure, CancellationToken token)
    {
        var results = members.Select(x => new QOperationResult { UserId = x.UserId, Status = QOperationStatus.NotExecuted }).ToArray();
        for (var i = 0; i < members.Count; i++)
        {
            if (token.IsCancellationRequested) throw new QBatchOperationCanceledException(new() { Items = results }, token);
            try
            {
                await execute(members[i], token).ConfigureAwait(false);
                results[i] = results[i] with { Status = QOperationStatus.Succeeded };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                results[i] = results[i] with { Status = QOperationStatus.Unknown, ErrorMessage = "Canceled during request; server outcome is unknown." };
                throw new QBatchOperationCanceledException(new() { Items = results }, token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results[i] = results[i] with { Status = isDefinitiveFailure(ex) ? QOperationStatus.Failed : QOperationStatus.Unknown,
                    ErrorMessage = ex.GetType().Name + ": " + (ex.Message.Length > 512 ? ex.Message[..512] : ex.Message) };
            }
        }
        return new() { Items = results };
    }
}
