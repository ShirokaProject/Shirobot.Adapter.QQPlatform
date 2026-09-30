# Offical QQAdapter

基于 QQ Bot OpenAPI v2 的 QQ 官方机器人适配器。

开发对接 QQ Open 平台日期：**2026-10-01**。

## Config 配置

首次加载适配器后，编辑适配器配置目录中的 `config.toml`。字段名使用小写下划线格式，填写后重启适配器生效。

```toml
# 必填：填写 QQ 开放平台中该机器人的 AppID 和 AppSecret
app_id = "你的 AppID"
app_secret = "你的 AppSecret"

# 以下为可选配置，通常保留默认值即可
intents = 100663296
shard_id = 0
shard_count = 1
token_endpoint = "https://api.bot.qq.com/app/getAppAccessToken"
api_base_url = "https://api.bot.qq.com/"
trace_events = false
reconnect_minimum_seconds = 2
reconnect_maximum_seconds = 30
startup_timeout_seconds = 30
```

| 字段 | 填写说明 |
| --- | --- |
| `app_id` | 必填，机器人应用的 AppID，按字符串填写。 |
| `app_secret` | 必填，同一机器人应用的 AppSecret；请勿提交到公开仓库。 |
| `intents` | 事件订阅位掩码，默认 `100663296`，即 `(1 << 25) \| (1 << 26)`。必须大于 `0`。 |
| `shard_id` / `shard_count` | 当前分片编号 / 总分片数，单实例填写 `0` / `1`；总数必须大于 `0`，编号从 `0` 开始且小于总数。 |
| `token_endpoint` | 获取访问令牌的 HTTPS 地址，默认值见示例。 |
| `api_base_url` | OpenAPI 的 HTTPS 基础地址，默认值见示例。 |
| `trace_events` | 是否输出收到的事件类型；排查问题时可设为 `true`，默认 `false`。 |
| `reconnect_minimum_seconds` / `reconnect_maximum_seconds` | 重连退避的最小 / 最大等待时间，单位秒，默认 `2` / `30`；最小值必须大于 `0`，最大值不得小于最小值。 |
| `startup_timeout_seconds` | 启动连接超时时间，单位秒，默认 `30`，必须大于 `0`。 |

## 许可证

[GPL3](./LICENSE)
