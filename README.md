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

## 媒体文件发送

图片、视频、语音和文件消息段支持 HTTP(S) URL、本地绝对/相对路径、`file://` URI，以及 `base64:内容` / `base64://内容`。Base64 解码后通过本地文件分片上传，上传结束或失败后自动删除临时文件，解码后的大小须为 1 字节至 200 MB。

Base64 使用原始字符串的内存切片，分块解码并写入临时文件，不复制完整编码字符串或创建完整解码字节数组。解码前检查大小，写入时再次限制大小；解码缓冲区大小固定。插件生成的原始 Base64 字符串仍会占用内存，大文件建议使用本地路径或文件流。

已发送消息的回复缓存仅保留 Base64 媒体的类型、文件名和内容省略标记，不保留文件内容，不能用缓存中的该 URI 重新发送。适配器 API 错误会省略 Base64 内容；新版宿主也会在插件和适配器的日志入口统一省略此类内容。

```csharp
new FileSegment("base64:" + Convert.ToBase64String(bytes)) { FileName = "report.pdf" }
```

建议指定实际的 `FileName`。未指定时，图片、视频、语音和文件分别使用 `image.png`、`video.mp4`、`audio.silk` 和 `file.bin`；默认名称不会转换文件格式。

## 许可证

[GPL3](./LICENSE)

### 群成员事件与官方群管理

`GROUP_MEMBER_ADD` / `GROUP_MEMBER_REMOVE` 映射为 SDK 的 `MemberJoinedEvent` / `MemberLeftEvent`，
`Raw` 为 `QOfficialGroupMemberEvent`，以 `member_openid` 作为群内用户 ID，另保留 `user_openid`。
`GROUP_JOIN_REQUEST` 映射为 `PlatformEvent`，Kind 为 `QEventKinds.OfficialGroupJoinRequest`，
Raw 为 `QOfficialJoinRequest`，保留申请 ID、验证消息/问答、邀请人、风险提示及自动审批策略 ID。
在适配器配置中开启 `subscribe_group_member_events = true`，或在 `intents` 中加入 `1 << 24`，
即可订阅上述事件。开关默认关闭；入群申请事件需要机器人是群管理员。

通过 `context.GetAdapterExtension<IQOfficialGroupApi>()` 获取官方群管理接口，支持：

- 分页查询入群申请、同意或拒绝（可提供拒绝理由及同时拉黑）。
- 查询群禁言状态、定时/周期禁言规则，以及单次最多 20 人的批量禁言/解除禁言。
- 自动审批策略的分页查询、创建、修改、删除、触发执行及白名单号码增删。

群管理需要管理员身份及 QQ 平台开放的接口权限；不满足权限时平台错误会返回调用方。
批量禁言通过 `UpdateExisting` 选择 add/update，零时长发送 del；只调用一次，不自动重复管理操作。
策略执行为异步任务，接口成功只代表已触发扫描。

按钮支持 `GroupId`（仅回调按钮）、`QKeyboardModal` 二次确认和 `Red` / `BlueFilled` 样式。
Markdown 可设置 `ForceVerifyImageResource = true`，图片转存失败时拒绝整条消息。
通用消息发送支持文字加单个媒体；纯媒体仍不填充空格正文。
类型化 `IQOfficialMessageApi.SendAsync` 的取消令牌覆盖最终发送请求。

这些新能力新增了共享 QQ Model 类型，测试时需同步更新宿主和适配器；旧插件无需重新编译，
使用新能力的插件需引用更新后的 `ShiroBot.Model.QQ`。
