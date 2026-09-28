# QQPlatform 回响探针

这个临时插件注册 `#qqping`，收到群聊或私聊命令后通过 `ReplyAsync` 回复 `QQPlatform pong`。它能同时检查 QQPlatform 事件接收、ShiroBot 插件路由及官方 QQ 消息发送。

在适配器仓库根目录运行：

```powershell
dotnet publish .\samples\ShiroBot.Plugin.QQPlatformEchoProbe\ShiroBot.Plugin.QQPlatformEchoProbe.csproj -c Release -o .\artifacts\echo-probe
```

将 `artifacts\echo-probe` 中的探针 DLL 放入宿主的 `plugins\ShiroBot.Plugin.QQPlatformEchoProbe` 目录，在宿主控制台执行 `load qqplatform.echo-probe` 即可热加载。在已添加机器人的 QQ 群里发送 `@机器人 #qqping`，预期收到 `QQPlatform pong`。如果机器人配置允许接收不带 @ 的群消息，也可以直接发送 `#qqping`。私聊可发送 `#qqping`。

测试后在宿主控制台执行 `unload qqplatform.echo-probe`，再删除 `plugins\ShiroBot.Plugin.QQPlatformEchoProbe` 目录，即可卸载探针。
