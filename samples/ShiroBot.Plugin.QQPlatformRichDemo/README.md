# QQPlatform RichDemo

参考 `MyBot.Plugins.RichDemo` 的 ShiroBot 示例插件。群聊和私聊均可使用 `#` 或 `/` 前缀。

| 命令 | 行为 |
| --- | --- |
| `#actiontest` | Markdown 按钮菜单；回调按钮可在页面间导航，也可用 `#action/features` 等文本命令 |
| `#mdtest` | QQPlatform Markdown 文章与返回按钮；没有权限或发送失败时回退为普通文本 |
| `#imagetest` | 发送配置的公网 HTTP(S) 图片 |
| `#recalltest` | 回复一条消息，三秒后撤回 |
| `#typingtest` | 私聊发送 3 秒输入状态，随后回复结果 |
| `#streamtest` | 私聊逐步更新同一条流式消息 |
| `#rich` | 显示帮助 |
| `@机器人` | 群聊中引用原消息并 @ 发送者；已匹配的演示命令优先处理 |

首次加载后，在插件目录的 `config.toml` 中设置 `image_url`，例如 `image_url = "https://example.org/test.png"`。QQPlatform 目前只接受公网 HTTP(S) 图片；本地文件路径不能直接用于这个演示。按钮需要 QQ 平台为机器人开放 Markdown 与自定义 Keyboard 权限；权限不足时插件会显示文本菜单。

Markdown、Keyboard 和按钮事件使用主仓库 `ShiroBot.Model.QQ` 的模型。示例通过 `IQOfficialMessageApi` 发送官方富消息，监听 `QOfficialButtonInteraction`；输入状态和流式消息通过主仓库的 `IQOfficialDirectMessageApi` 能力接口调用。插件不引用具体适配器程序集。

输入状态和流式消息只支持 QQ 私聊。流式接口的 `AppendAsync` 接收截至当前的完整文本，后续文本须保留前一次内容作为前缀；最后调用 `CompleteAsync`。

构建：

```powershell
dotnet build .\ShiroBot.Plugin.QQPlatformRichDemo.csproj -c Release
```

构建时需要同级目录 `../Shirobot` 的主仓库源码；其他位置可传入 `-p:ShiroBotSourceRoot=主仓库路径`。

将构建出的 `ShiroBot.Plugin.QQPlatformRichDemo.dll` 放到宿主的 `plugins/ShiroBot.Plugin.QQPlatformRichDemo` 目录；QQPlatform 适配器也需已安装。
