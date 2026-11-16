using System.Text.Json.Nodes;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;

/// <summary>Optional auto-approval strategy endpoints, kept separate from basic group management.</summary>
internal sealed class QQGroupApprovalService(QQOpenApiClient api) : IQGroupApprovalStrategyApi
{
    public async Task<QApprovalStrategyPage> GetApprovalStrategiesAsync(string? cursor = null, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await api.SendGroupRequestAsync(HttpMethod.Get,
            QQGroupHelpers.Page("v2/groups/join_approval_strategy", cursor, limit), null, cancellationToken)
            .ConfigureAwait(false);
        return new QApprovalStrategyPage(
            QQOfficialGroupMapper.Items(result, "strategies").Select(x => QQOfficialGroupMapper.Strategy(x)).ToArray(),
            QQOfficialGroupMapper.Text(result, "next_cursor"));
    }

    public async Task<QApprovalStrategy> CreateApprovalStrategyAsync(QApprovalStrategyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var body = new JsonObject { ["is_enable"] = options.Enabled ? "on" : "off" };
        QQGroupHelpers.Groups(body, options.GroupIds, options.GroupNumbers);
        if (options.ExpiresAt is { } expiry) body["expire_at"] = QQGroupHelpers.Stamp(expiry);
        QQGroupHelpers.Remark(body, options.Remark);
        return QQOfficialGroupMapper.Strategy(await api.SendGroupRequestAsync(HttpMethod.Post,
            "v2/groups/join_approval_strategy", body, cancellationToken).ConfigureAwait(false));
    }

    public async Task<QApprovalStrategy> UpdateApprovalStrategyAsync(string strategyId,
        QApprovalStrategyUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var body = new JsonObject();
        if (update.Enabled is { } enabled) body["is_enable"] = enabled ? "on" : "off";
        if (update.ExpiresAt.IsSpecified)
        {
            // No verified reset mapping in this adapter: do not silently drop the requested clear.
            if (update.ExpiresAt.IsClear)
                throw new NotSupportedException("This adapter does not implement clearing a strategy expiry.");
            body["expire_at"] = QQGroupHelpers.Stamp(update.ExpiresAt.Value);
        }
        if (update.Remark.IsSpecified)
            QQGroupHelpers.Remark(body, update.Remark.IsClear ? string.Empty : update.Remark.Value);
        if (update.Groups is { } groups)
        {
            var action = new JsonObject { ["op"] = groups.Add ? "add" : "del" };
            QQGroupHelpers.Groups(action, groups.GroupIds, groups.GroupNumbers);
            body["group_action"] = action;
        }
        if (body.Count == 0) throw new ArgumentException("No strategy changes supplied.", nameof(update));
        return QQOfficialGroupMapper.Strategy(await api.SendGroupRequestAsync(HttpMethod.Patch,
            QQGroupHelpers.StrategyRoute(strategyId), body, cancellationToken).ConfigureAwait(false), strategyId);
    }

    public async Task DeleteApprovalStrategyAsync(string strategyId,
        CancellationToken cancellationToken = default) =>
        await api.SendGroupRequestAsync(HttpMethod.Delete, QQGroupHelpers.StrategyRoute(strategyId), null,
            cancellationToken).ConfigureAwait(false);

    public async Task ExecuteApprovalStrategyAsync(string strategyId,
        CancellationToken cancellationToken = default) =>
        await api.SendGroupRequestAsync(HttpMethod.Post,
            QQGroupHelpers.StrategyRoute(strategyId) + "/execute", new JsonObject(), cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> UpdateApprovalWhitelistAsync(string strategyId, IReadOnlyList<string> qqNumbers,
        bool add = true, CancellationToken cancellationToken = default)
    {
        var users = QQGroupHelpers.Strings(qqNumbers, 10000);
        if (qqNumbers.Any(x => !ulong.TryParse(x, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number == 0))
            throw new ArgumentException("Whitelist entries must be QQ numbers.", nameof(qqNumbers));
        var result = await api.SendGroupRequestAsync(HttpMethod.Post,
            QQGroupHelpers.StrategyRoute(strategyId) + "/whitelist_users",
            new JsonObject { ["op"] = add ? "add" : "del", ["whitelist_users"] = users }, cancellationToken)
            .ConfigureAwait(false);
        return QQOfficialGroupMapper.Number(result, "whitelist_user_count");
    }
}
