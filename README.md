# ShiroBot.Adapter.QQPlatform

ShiroBot 的 QQ 官方机器人适配器，基于 QQ Bot OpenAPI v2。支持群聊与私聊消息、Markdown、按钮、Ark 模板，以及私聊输入状态和流式回复。平台 ID：`qq-official`。

## 项目结构

```text
QQPlatformAdapter.cs                生命周期与事件入口
|-- AdapterImpl/
|   |-- QQMessageService.cs         通用消息收发
|   |-- QQOfficialMessageService.cs 官方富消息能力
|   `-- QQPlatformMessageStream.cs  私聊流式回复
|-- Protocol/
|   |-- QQGatewayClient.cs          WebSocket 连接与事件
|   |-- QQEventTranslator.cs        事件转换
|   `-- QQOpenApiClient.cs          HTTP API 调用
|-- Wire/                          QQ 请求与响应类型
|-- tests/                         适配器测试
`-- samples/                       示例插件
```

## 许可证

与上游 ShiroBot 一致，使用 GNU General Public License v3.0。详见 [LICENSE](./LICENSE)。
