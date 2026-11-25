# Shirobot.Adapter.QQPlatform

ShiroBot 适配器生态中的一员，负责接入 QQ 官方机器人。

## 功能

接入：Access Token 鉴权、WebSocket 网关、心跳与断线重连。

| 能力 | 单聊 | 群聊 |
| --- | :-: | :-: |
| 文本、Markdown、图片、语音、视频、文件 | ✓ | ✓ |
| 引用回复、撤回 | ✓ | ✓ |
| 按钮交互、图文卡片 | ✓ | ✓ |
| 用户资料、头像 | ✓ | ✓ |
| 流式消息 | ✓ | — |
| 群资料、成员、群昵称、禁言、入群审批 | — | ✓ |
| 群黑名单、机器人群内状态（内邀） | — | ✓ |

其他成员或私聊对方撤回消息时，QQ 不提供撤回事件，机器人无法感知。

完整能力与事件见 [CAPABILITIES.md](./CAPABILITIES.md)，实现分层见 [ARCHITECTURE.md](./ARCHITECTURE.md)。

### [官方更新跟进](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html)

最近维护：2026-11-17

| 日期 | 更新内容 |
| --- | --- |
| [**20261117**](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html#_20261117) | 群消息被动回复有效期延长到 60 分钟；开发者是群主或管理员时每条群消息可回复 50 次（配置“开发者是群主或管理员”）；群消息可在私聊被动回复发送者，与群内回复共用次数。 |
| [**20261116**](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html#_20261116) | 用户资料、昵称、头像、群昵称，以及群统一标识 `union_group_openid`。 |
| [**20261028**](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html#_20261028) | 群聊、私聊图文卡片。 |
| [**20261026**](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html#_20261026) | 群聊文本和 Markdown @全体成员。 |
| [**20261021**](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html#_20261021) | 图片素材管理，Markdown 里可用 `qqbot-media://` 引用图片。 |
| [**20260903**](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html#_20260903) | 群成员列表、群成员信息、群成员批量移除。 |
| [**20260810**](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html#_20260810) | 群禁言设置与查询、入群申请审批、入群自动审批策略与入群申请事件。 |

## 安装与使用

按 [ShiroBot 文档](https://docs.shiroka.org/adapter/) 部署，构建使用 .NET 11，宿主与 QQ Model 需一同更新。

在适配器的 `config.toml` 填入 `app_id` 和 `app_secret` 后重启。加载 `samples` 里的 RichDemo 后发送 `#rich` 可查看演示命令；`#capabilities` 会为每项能力给出对应测试按钮，`#groupinfo` 展示群资料、机器人状态与群黑名单的真实可用情况。

## 贡献者

感谢每一位贡献代码和反馈问题的朋友。

<a href="https://github.com/ShirokaProject/Shirobot.Adapter.QQPlatform/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=ShirokaProject/Shirobot.Adapter.QQPlatform" alt="贡献者" />
</a>

## 许可证

[Apache License 2.0](./LICENSE)
