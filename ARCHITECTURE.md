# 架构

QQ 官方适配器按“端口与适配器”组织：网络与协议细节向内收敛，ShiroBot 只看到稳定的服务契约。  
所有代码仍在同一个程序集内，便于单文件插件打包；分层依靠目录、命名空间和单向依赖约束。

## 分层

| 层 | 命名空间 | 职责 | 允许依赖 |
| --- | --- | --- | --- |
| Transport | `...Transport` / `...Transport.Gateway` | HTTP 发送、Token 刷新、分片上传、WebSocket 连接、心跳、重连、会话恢复 | `Wire`、配置 |
| Wire | `...Wire` | QQ 原始 DTO、JSON 上下文、API 路由 | 无 |
| Protocol | `...Protocol` + `.Mapping` + `.Support` | OpenAPI 门面、语义化发送内容、事件翻译、字段映射、协议降级 | `Wire`、`Model.QQ`、`SDK.Models` |
| AdapterImpl | `...AdapterImpl` + `.Messaging` + `.Official` + `.Groups` + `.Dispatch` | ShiroBot 服务实现、消息上下文、被动回复窗口、群/用户/媒体能力 | `Protocol`、`Model.QQ`、`SDK` |
| Composition | `QQPlatformAdapter.cs` | 生命周期、依赖装配、扩展服务暴露 | 全部内部层 |

依赖只能向下：`AdapterImpl -> Protocol -> Wire`，`Transport -> Wire`。  
`Wire` 不引用 ShiroBot SDK；`Protocol` 是唯一把 QQ 字段翻译成 `Model.QQ` / `SDK.Models` 的层；`AdapterImpl` 不再构造 Wire DTO。

## 结构

```
ShiroBot.Adapter.QQPlatform/
├─ QQPlatformAdapter.cs              composition root：Start/Stop、装配、扩展入口
├─ QQPlatformConfig.cs
├─ Transport/
│  ├─ QQApiTransport.cs              Token 注入、401 刷新、429/5xx 重试
│  ├─ QQTokenProvider.cs
│  ├─ QQChunkedUploadClient.cs
│  ├─ QQLogSanitizer.cs
│  └─ Gateway/
│     ├─ QQGatewayConnection.cs      WSS 连接、心跳、重连、恢复、串行派发队列
│     └─ QQGatewayClosePolicy.cs
├─ Wire/
│  ├─ QQModels.cs                    原始 JSON DTO
│  ├─ QQApiRoutes.cs                 路由构造
│  └─ QQJson.cs                      source-generated JSON
├─ Protocol/
│  ├─ QQOpenApiClient.cs             OpenAPI 门面：语义发送、上传、群/用户操作
│  ├─ QQOutboundContent.cs           QQReplyContext / 语义化发送内容 / 流帧
│  ├─ QQEventTranslator.cs           GatewayPayload -> BotEvent
│  ├─ QQMaterialClient.cs            Markdown 图片素材
│  ├─ Mapping/                       字段与富文本映射、引用解析
│  └─ Support/                       有界缓存、上传缓存
└─ AdapterImpl/
   ├─ QQEventService.cs
   ├─ QQUserService.cs
   ├─ QQChannelService.cs
   ├─ Dispatch/
   │  ├─ QQInboundDispatcher.cs       去重、翻译、互动回应、群名补充、发布
   │  └─ QQGroupNameCache.cs
   ├─ Messaging/
   │  ├─ QQMessageService.cs          IMessageService 稳定门面
   │  ├─ QQOutboundMessagePipeline.cs 段编码、分片、被动/主动发送
   │  ├─ QQIncomingMessageContext.cs  最近消息、引用链、互动回复
   │  ├─ QQPassiveReplyWindow.cs      msg_seq 与被动回复窗口
   │  ├─ QQReplyTracker.cs
   │  ├─ QQMessageCapabilities.cs
   │  ├─ QQTextChunker.cs
   │  └─ QQMediaSource.cs
   ├─ Official/
   │  ├─ QQOfficialMessageService.cs  文本、Markdown、卡片、Ark、Embed、互动、流式
   │  ├─ QQOfficialMediaService.cs    上传、媒体发送、IFileService
   │  └─ QQOfficialMessageStream.cs
   └─ Groups/
      ├─ QQGroupService.cs            IQGroupApi
      ├─ QQGroupApprovalService.cs    IQGroupApprovalStrategyApi
      ├─ QQGroupHelpers.cs
      └─ QBatchMuteExecutor.cs
```

## 运行时

```
                 +---------------- ShiroBot Host ----------------+
                 | IMessageService IChannelService IUserService |
                 | IEventService   IQ* extensions               |
                 +----------------------+-----------------------+
                                        |
                       +----------------v-----------------+
                       | QQPlatformAdapter (composition)  |
                       +---+----------+----------+--------+
                           |          |          |
                  +--------v--+  +----v-----+  +-v----------------+
                  | Dispatcher|  | Messaging|  | Official / Group |
                  +-----+-----+  +----+-----+  +----+-------------+
                        |             |             |
                        +-------------+-------------+
                                      |
                         +------------v-------------+
                         | Protocol (semantic API)   |
                         | OpenApiClient + Mapping   |
                         +------------+--------------+
                                      |
                    +-----------------+-----------------+
                    |                                   |
          +---------v---------+               +---------v---------+
          | Transport (HTTP)  |               | Transport (WSS)   |
          | token/retry/upload|               | heartbeat/resume  |
          +---------+---------+               +---------+---------+
                    |                                   |
                    +---------- QQ OpenAPI / Gateway ----+
```

### 入站

```
WSS frame
  -> QQGatewayConnection: hello / heartbeat / resume / shard
  -> bounded channel(1024) -> serial dispatch
  -> QQInboundDispatcher
       de-duplicate event id
       QQEventTranslator.Translate -> BotEvent
       INTERACTION_CREATE -> early acknowledge + RememberInteraction
       group event -> QQGroupNameCache enrichment
       MessageEvent -> QQIncomingMessageContext.RegisterIncoming
  -> QQEventService.PublishAsync -> host
```

### 出站

```
host
  -> QQMessageService
       -> QQOutboundMessagePipeline
            capabilities / segments / chunking / media
            QQPassiveReplyWindow: msg_seq + group 60min/5reply (50 when developer is owner/admin) or C2C 60min/4reply; limits follow the source message
            QQOutboundContent (Text / Markdown / Card / Ark / Embed / Media)
       -> QQOpenApiClient.SendAsync
            BuildRequest + ApplyReply + JSON
       -> QQApiTransport: token / retry / rate limit
  -> IQOfficialMessageApi
       -> QQOfficialMessageService -> same pipeline / protocol
  -> IQOfficialMediaApi / IFileService
       -> QQOfficialMediaService -> upload -> pipeline
  -> IQGroupApi / IQGroupApprovalStrategyApi
       -> QQGroupService / QQGroupApprovalService -> QQOpenApiClient
```

## 设计约束

- `Wire` 只描述 QQ 报文，不出现 SDK 类型。
- `Protocol` 拥有所有 DTO 构造、字段映射、协议特例（卡片校验、按钮必须 Markdown、`msg_seq`、被动回复过期降级、上传 `file_info`）。
- `AdapterImpl` 只处理 SDK 语义与状态：能力评估、消息上下文、引用链、被动回复窗口、服务契约。
- `QQPlatformAdapter` 只做装配与生命周期，不解析事件、不构造请求。
- 新 API 的落点：
  1. `Wire` 加 DTO/路由；
  2. `Protocol` 加语义方法或映射；
  3. `AdapterImpl` 加 ShiroBot 服务适配；
  4. `QQPlatformAdapter.GetExtension` 暴露新接口。

## 验证

```powershell
dotnet test tests/ShiroBot.QQPlatform.Tests/ShiroBot.QQPlatform.Tests.csproj -p:ShiroBotPluginPackagingEnabled=false
dotnet build ShiroBot.Adapter.QQPlatform.sln -c Release
```
