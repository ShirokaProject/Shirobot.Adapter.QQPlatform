# ShiroBot QQPlatform Adapter

独立的 QQ 官方机器人适配器，使用 QQ Bot OpenAPI v2。平台 ID 为 `qq-official`，可与 Milky 的 `qq` 同时安装。

## 结构

```text
QQPlatformAdapter                 宿主生命周期、事件入口
  ├─ QQGatewayClient              WebSocket Identify/Resume、心跳、重连
  ├─ QQEventTranslator            QQ Gateway 事件 → ShiroBot 事件
  ├─ QQMessageService             ShiroBot 消息段 → QQ 发送请求
  └─ QQOpenApiClient              具体 QQ OpenAPI 操作
       ├─ QQApiRoutes             统一路由
       ├─ QQApiTransport          鉴权、Token 刷新、429 重试、HTTP 错误
       └─ Wire/QQModels.cs        请求与响应 DTO
```

增加 QQ 接口时，先在 `Wire` 添加 DTO，在 `QQApiRoutes` 添加路径，再在 `QQOpenApiClient` 添加类型化方法。宿主扩展 API 放在 `Contracts`，插件通过 `GetExtension<IQQPlatformMessageService>()` 使用。平台事件中未做通用映射的部分以 `PlatformEvent` 和原始 JSON 保留。

## 已实现

- AppId/AppSecret 换取并缓存 Access Token，401 刷新，429 按 `Retry-After` 重试。
- 默认 Token 地址采用 `https://api.bot.qq.com/app/getAppAccessToken`；旧 `bots.qq.com` 配置在启动时迁移。OpenAPI 响应按 `err_code` 判定业务错误，并保留 `trace_id` 供排查。
- Gateway Identify/Resume、心跳、指数退避重连，事件 ID 去重。
- `GROUP_AT_MESSAGE_CREATE`、`GROUP_MESSAGE_CREATE` 与 `C2C_MESSAGE_CREATE` 转成 `MessageEvent`；其余事件转成 `PlatformEvent`。
- 消息内容中的 QQ 提及与表情标记转为 `MentionSegment`、`EmojiSegment`，命令匹配读取的纯文本不再包含这些标记。
- 群消息直接映射 `author.username`、`author.member_role`；`mentions` 中的昵称用于显示被提及用户。`author.union_openid` 保留在原始事件中。群名通过官方群资料接口查询并缓存，查询失败时继续处理消息，日志回退显示群 OpenID。
- 群成员详情接口仍属 QQ 官方内邀能力；普通消息处理不依赖它。
- 群聊/私聊文本回复、远程 HTTP(S) 图片上传与发送、Markdown/Ark 扩展、删除消息。
- 富消息支持原生 Markdown 加自定义 Keyboard（最多 5 行、每行 5 个按钮），以及 URL、回调、发送指令三类 Action。插件使用 `IQQPlatformMessageService.SendRichMarkdownAsync`；回调以 `QQInteractionEvent` 上报，适配器自动确认点击，插件用事件 ID 继续被动回复。
- 发送接口确认成功后，在 CLI 记录回复目标和内容；图片、Markdown 与 Ark 也有对应的发送日志。
- `msg_id` + 递增 `msg_seq` 被动回复。显式 `QuoteSegment` 优先；普通 `ReplyAsync` 使用同会话最近 5 分钟的入站消息 ID，方便现有 ShiroBot 插件使用。

## 配置

首次加载时，ShiroBot 会在该适配器目录生成 `config.toml`。填入：

```toml
app_id = "你的 QQ 机器人 AppId"
app_secret = "你的 QQ 机器人 AppSecret"
intents = 33554432
```

保持 `Intents` 默认值即可订阅 QQ 群、C2C 与按钮交互事件。旧配置若只有 `1<<25`，启动时会补上按钮交互的 `1<<26`。不要提交含真实密钥的配置文件；本仓库已忽略 `config.toml`。

## 构建和安装

在 Rider 中打开 `ShiroBot.Adapter.QQPlatform.sln`，使适配器、`Contracts`、示例和测试项目一同加载。`QQKeyboard` 等富消息类型定义在 `Contracts` 项目中。

```powershell
dotnet test .\ShiroBot.Adapter.QQPlatform.sln
dotnet publish .\ShiroBot.Adapter.QQPlatform.csproj -c Release -p:CopyAdapterToHost=false -o .\artifacts\publish
```

把 `artifacts/publish` 中的 `ShiroBot.Adapter.QQPlatform.dll` 和 `ShiroBot.QQPlatform.Contracts.dll` 放入 ShiroBot 的同一个适配器目录，再配置 `config.toml`。主仓库无需修改。

Markdown 和自定义按钮仍受 QQ 平台为机器人开放的权限限制；接口拒绝时会返回明确错误。接口依据：[腾讯 v2 发送消息](https://github.com/tencent-connect/bot-docs/blob/main/docs/develop/api-v2/server-inter/message/send-receive/send.md)、[Markdown](https://github.com/tencent-connect/bot-docs/blob/main/docs/develop/api-v2/server-inter/message/type/markdown.md)、[消息按钮与回调](https://github.com/tencent-connect/bot-docs/blob/main/docs/develop/api-v2/server-inter/message/trans/msg-btn.md)。

## 当前边界

QQ 官方接口返回的群聊标识是 `group_openid`，群成员是 `member_openid`，私聊用户是 `user_openid`。它们是不同作用域的 OpenID，不能当作数字 QQ 群号或 QQ 号，也不能直接与 Milky 的数字 ID 互换。适配器将这些官方标识原样放入 `Channel.Id` 与 `User.Id`，并在 `MessageEvent.Raw` 保留原始事件。


QQ 官方回复通常需要最近的入站 `msg_id`。没有有效入站消息时，发送会给出明确错误。本地图片、音频、视频、文件上传以及频道事件尚未实现；媒体发送目前只支持公网 HTTP(S) 图片地址。Markdown/Ark 的可用性取决于 QQ 平台给机器人开放的权限。真实账号联调还需 AppId/AppSecret 和测试群或联系人。

接口依据：[QQ 官方 v2 API](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/)、[群消息事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/group_at_message_create.html)、[群资料](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_info.get.html)、[群成员信息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_groups_group_openid_members_member_openid.get.html)。
