using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using ShiroBot.Adapter.QQPlatform.Wire;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal static class QQJson
{
    // Generated metadata avoids both global options and runtime reflection accessor caches.
    public static JsonSerializerOptions Options => QQJsonContext.Default.Options;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(GatewayResponse))]
[JsonSerializable(typeof(GatewayPayload))]
[JsonSerializable(typeof(HelloData))]
[JsonSerializable(typeof(ReadyData))]
[JsonSerializable(typeof(QQUser))]
[JsonSerializable(typeof(QQIncomingMessage))]
[JsonSerializable(typeof(QQInteractionData))]
[JsonSerializable(typeof(QQGroupInfo))]
[JsonSerializable(typeof(QQGroupMemberPage))]
[JsonSerializable(typeof(QQSendRequest))]
[JsonSerializable(typeof(QQSendResponse))]
[JsonSerializable(typeof(QQStreamRequest))]
[JsonSerializable(typeof(QQMuteRequest))]
[JsonSerializable(typeof(QQRemoveMembersRequest))]
[JsonSerializable(typeof(QQUploadRequest))]
[JsonSerializable(typeof(QQUploadResponse))]
[JsonSerializable(typeof(QQUploadPrepareRequest))]
[JsonSerializable(typeof(QQUploadPrepareResponse))]
[JsonSerializable(typeof(QQUploadPartFinishRequest))]
[JsonSerializable(typeof(QQUploadCompleteRequest))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(object))]
internal partial class QQJsonContext : JsonSerializerContext;
