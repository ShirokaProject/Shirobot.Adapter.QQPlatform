using ShiroBot.SDK.Config;

namespace ShiroBot.Plugin.QQPlatformRichDemo;

public sealed class RichDemoConfig
{
    [ConfigField("图片 URI：HTTP(S)、宿主本地路径、file:// 或 base64:；相对路径按宿主工作目录解析",
        Label = "测试图片来源")]
    public string ImageUrl { get; set; } = "";

    [ConfigField("图文卡片标题，1–50 字符", Label = "卡片标题")]
    public string CardTitle { get; set; } = "ShiroBot 能力演示";

    [ConfigField("图文卡片描述，1–200 字符", Label = "卡片描述")]
    public string CardDescription { get; set; } = "QQ 官方图文卡片：群聊、私聊均可测试。";

    [ConfigField("卡片跳转 HTTPS 地址，不超过 1024 字符", Label = "卡片跳转 URL")]
    public string CardUrl { get; set; } = "https://bot.q.qq.com/wiki/develop/api-v2/changelog.html";

    [ConfigField("卡片封面须为公网 HTTPS 图片，不能使用本地路径或 qqbot-media 引用",
        Label = "卡片图片 URL")]
    public string CardImageUrl { get; set; } = "";

    [ConfigField("危险演示：宿主管理员可真实通知全群；机器人须为群管理员，每群每天仅一次",
        Label = "允许全体通知演示")]
    public bool EnableEveryoneDemo { get; set; }
}
