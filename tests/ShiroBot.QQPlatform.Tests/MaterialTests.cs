using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Model.QQ;
using Xunit;

using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Adapter.QQPlatform.Transport.Gateway;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Official;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;
using ShiroBot.Adapter.QQPlatform.Protocol.Support;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
namespace ShiroBot.QQPlatform.Tests;

public sealed class MaterialTests
{
    [Fact]
    public async Task UploadHashesContentAndDoesNotSendBotCredentialsToCos()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var api = handler.CreateApi(http);
        IQOfficialMaterialApi materials = api.Materials;
        using var stream = new MemoryStream([1, 2, 3, 4]);
        stream.Position = 1;
        var material = await materials.UploadImageAsync(stream, "test.png");
        Assert.True(stream.CanRead);
        Assert.Equal("image-1", material.ImageId);
        Assert.Equal("qqbot-media://image/v1/token", material.Url);
        var put = handler.Requests.Single(x => x.Method == "PUT");
        Assert.Null(put.Authorization);
        Assert.Equal("image/png", put.ContentType);
        Assert.Equal(new byte[] { 2, 3, 4 }, put.Body);
        var prepare = handler.Requests.Single(x => x.Path.EndsWith("/prepare"));
        using var json = JsonDocument.Parse(prepare.Body);
        Assert.Equal(3, json.RootElement.GetProperty("file_size").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(new byte[] { 2, 3, 4 })), json.RootElement.GetProperty("md5").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(new byte[] { 2, 3, 4 })), json.RootElement.GetProperty("sha1").GetString());
        var upload = handler.Requests.Single(x => x.Method == "PUT");
        Assert.Null(upload.Authorization);
        Assert.Equal("image/png", upload.ContentType);
        Assert.Equal(new byte[] { 2, 3, 4 }, upload.Body);
        Assert.Equal("QQBot token", prepare.Authorization);
    }

    [Theory]
    [InlineData("x.txt", 1)]
    [InlineData("../x.png", 1)]
    [InlineData("x.png", 0)]
    [InlineData("x.png", 10485761)]
    public async Task InvalidImageIsRejectedBeforeAnyHttpRequest(string name, int length)
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        using var stream = new MemoryStream(new byte[length]);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => handler.CreateApi(http).Materials.UploadImageAsync(stream, name));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CanceledUploadDoesNotMakeRequests()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        using var stream = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.CreateApi(http).Materials.UploadImageAsync(stream, "x.png", new CancellationToken(true)));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("put")]
    [InlineData("etag")]
    [InlineData("url")]
    public async Task FailedUploadNeverCompletes(string failure)
    {
        using var handler = new ChangelogHandler { UploadFailure = failure };
        using var http = new HttpClient(handler);
        using var stream = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<Exception>(() => handler.CreateApi(http).Materials.UploadImageAsync(stream, "x.png"));
        Assert.DoesNotContain(handler.Requests, x => x.Path.EndsWith("/complete"));
    }

    [Fact]
    public async Task ListAndDeleteEncodeIdentifiers()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var api = handler.CreateApi(http).Materials;
        var page = await api.GetImagesAsync("a&b");
        Assert.Equal("image-1", Assert.Single(page.Images).ImageId);
        Assert.Equal(1, page.Total);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), page.Images[0].CreatedAt);
        await api.DeleteImageAsync("image/1");
        Assert.Contains(handler.Requests, x => x.Path.EndsWith("?cursor=a%26b"));
        Assert.Contains(handler.Requests, x => x.Method == "DELETE" && x.Path.EndsWith("image%2F1/delete"));
    }
}

internal sealed class ChangelogHandler : HttpMessageHandler
{
    public List<(string Method, string Path, string? Authorization, string? ContentType, byte[] Body)> Requests { get; } = [];
    public string? UploadFailure { get; init; }
    public bool UserInfoError { get; init; }

    public QQOpenApiClient CreateApi(HttpClient http)
    {
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        return new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add((request.Method.Method, path, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));
        if (UserInfoError && path.StartsWith("/v2/users/") && path.EndsWith("/info"))
            return Json("""{"err_code":40184001,"message":"no friend relationship"}""", HttpStatusCode.Forbidden);
        if (request.Method == HttpMethod.Put)
        {
            var result = Json("{}", UploadFailure == "put" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            result.Headers.ETag = new('"' + (UploadFailure == "etag" ? "bad" : Convert.ToHexStringLower(MD5.HashData(body))) + '"');
            return result;
        }
        return Json(path switch
        {
            "/app/getAppAccessToken" => """{"access_token":"token","expires_in":7200}""",
            "/v2/media-images/prepare" => UploadFailure == "url"
                ? """{"upload_id":"upload-1","presigned_url":"http://upload.example/image"}"""
                : """{"upload_id":"upload-1","presigned_url":"https://upload.example/image"}""",
            "/v2/media-images/complete" => """{"image_id":"image-1","url":"qqbot-media://image/v1/token","ttl":7776000}""",
            _ when path.StartsWith("/v2/users/") && path.EndsWith("/info") => """{"username":"用户昵称","avatar":"https://example.org/avatar.png","union_openid":"union-user","allow_proactive_msg":true}""",
            "/v2/groups/g/info" => """{"group_openid":"g","group_name":"群名","union_group_openid":"union-group","group_member_num":10}""",
            _ when path.EndsWith("/messages") => """{"id":"sent-1"}""",
            _ when path.StartsWith("/v2/media-images?") => """{"images":[{"image_id":"image-1","url":"qqbot-media://image/v1/token","created_at":1,"expires_at":2}],"next_cursor":"","total":1}""",
            _ => "{}"
        });
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
