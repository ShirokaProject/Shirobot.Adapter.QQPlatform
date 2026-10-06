using System.Text;
using System.Net;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.SDK.Models;
using Xunit;

namespace ShiroBot.QQPlatform.Tests;

public sealed class MediaSourceTests
{
    [Theory]
    [InlineData("base64:")]
    [InlineData("base64://")]
    [InlineData("BASE64:/")]
    public void ApiErrorsOmitBase64AndKeepDiagnosticCodes(string prefix)
    {
        var exception = new QQApiException("/v2/groups/g/files", HttpStatusCode.BadRequest, 40034005,
            "Invalid file " + prefix + new string('A', 100000), "trace-1");
        Assert.Contains("40034005", exception.Message);
        Assert.Contains("trace-1", exception.Message);
        Assert.Contains("[Base64 内容已省略]", exception.Message);
        Assert.True(exception.Message.Length < 256);
    }

    [Theory]
    [InlineData(12287)]
    [InlineData(12288)]
    [InlineData(12289)]
    [InlineData(24576)]
    [InlineData(24577)]
    [InlineData(1048576)]
    public async Task Base64DecodesAcrossChunksWithBoundedWrites(int length)
    {
        var expected = new byte[length];
        new Random(42).NextBytes(expected);
        var encoded = Convert.ToBase64String(expected, Base64FormattingOptions.InsertLineBreaks);
        using var output = new MeasuringStream();
        await QQMediaSource.WriteBase64Async((encoded + " \t\r\n").AsMemory(), output);
        Assert.Equal(expected, output.ToArray());
        Assert.InRange(output.MaxWriteSize, 1, 12 * 1024);
        if (length > 12 * 1024) Assert.True(output.WriteCount > 1);
    }

    [Theory]
    [InlineData("YWJjZA==", 4, true)]
    [InlineData("YWJjZGU=", 5, true)]
    [InlineData("YWJjZGVm", 6, true)]
    [InlineData("YWJjZA==", 3, false)]
    [InlineData("YWJjZGU=", 4, false)]
    [InlineData("YWJjZGVm", 5, false)]
    [InlineData("YWJj\nZGVm", 5, false)]
    public async Task SizeLimitIsExactAndRejectsBeforeWriting(string encoded, int limit, bool accepted)
    {
        using var output = new MeasuringStream();
        if (accepted)
        {
            await QQMediaSource.WriteBase64Async(encoded.AsMemory(), output, limit);
            Assert.Equal(limit, output.Length);
        }
        else
        {
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                QQMediaSource.WriteBase64Async(encoded.AsMemory(), output, limit));
            Assert.Equal(0, output.WriteCount);
        }
    }

    [Theory]
    [InlineData("AA=A")]
    [InlineData("====")]
    [InlineData("YQ==YQ==")]
    [InlineData("YWJj\u00a0ZGVm")]
    public async Task MalformedBase64IsRejected(string encoded)
    {
        using var output = new MeasuringStream();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            QQMediaSource.WriteBase64Async(encoded.AsMemory(), output));
    }

    [Fact]
    public async Task PaddingCannotTerminateANonFinalChunk()
    {
        var encoded = new string('A', 16 * 1024 - 4) + "YQ==YQ==";
        using var output = new MeasuringStream();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            QQMediaSource.WriteBase64Async(encoded.AsMemory(), output));
    }

    [Fact]
    public async Task LargeBase64FileRoundTripsAndIsCleanedUp()
    {
        var expected = new byte[1024 * 1024];
        new Random(42).NextBytes(expected);
        var source = await QQMediaSource.ResolveAsync(
            new FileSegment("base64://" + Convert.ToBase64String(expected)));
        var path = source.Path!;
        try { Assert.Equal(expected, await File.ReadAllBytesAsync(path)); }
        finally { source.Dispose(); }
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("base64:")]
    [InlineData("base64://")]
    [InlineData("BASE64:")]
    public async Task Base64DecodesToTemporaryFileAndDisposesIt(string prefix)
    {
        var source = await QQMediaSource.ResolveAsync(new FileSegment(prefix + "YWJj\nZGVm")
            { FileName = "report.pdf" });
        var path = source.Path!;
        try
        {
            Assert.Null(source.Url);
            Assert.Equal("report.pdf", source.FileName);
            Assert.Equal(Encoding.ASCII.GetBytes("abcdef"), await File.ReadAllBytesAsync(path));
        }
        finally { source.Dispose(); }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TemporaryFileIsDisposedWhenOperationFails()
    {
        string? path = null;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var source = await QQMediaSource.ResolveAsync(new FileSegment("base64:YWJjZGVm"));
            path = source.Path;
            throw new IOException("Upload failed.");
        });
        Assert.NotNull(path);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("base64:invalid!")]
    [InlineData("base64://a")]
    public async Task InvalidBase64IsRejected(string uri)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            QQMediaSource.ResolveAsync(new FileSegment(uri)));
        Assert.DoesNotContain(uri, exception.Message);
    }

    [Theory]
    [InlineData("base64:")]
    [InlineData("base64://")]
    [InlineData("base64: \n")]
    public async Task EmptyBase64IsRejected(string uri)
        => await Assert.ThrowsAsync<NotSupportedException>(() =>
            QQMediaSource.ResolveAsync(new FileSegment(uri)));

    [Theory]
    [InlineData("image", "image.png")]
    [InlineData("video", "video.mp4")]
    [InlineData("audio", "audio.silk")]
    [InlineData("file", "file.bin")]
    public async Task Base64MediaGetsDefaultFileName(string type, string expectedName)
    {
        const string uri = "base64:YWJjZGVm";
        ResourceSegment resource = type switch
        {
            "image" => new ImageSegment(uri),
            "video" => new VideoSegment(uri),
            "audio" => new AudioSegment(uri),
            _ => new FileSegment(uri)
        };
        using var source = await QQMediaSource.ResolveAsync(resource);
        Assert.Equal(expectedName, source.FileName);
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("relative")]
    [InlineData("file")]
    public async Task LocalPathsArePreservedAndNeverDeleted(string kind)
    {
        var path = Path.GetTempFileName();
        try
        {
            var uri = kind switch
            {
                "relative" => Path.GetRelativePath(Environment.CurrentDirectory, path),
                "file" => new Uri(path).AbsoluteUri,
                _ => path
            };
            using (var source = await QQMediaSource.ResolveAsync(new FileSegment(uri)))
                Assert.Equal(path, source.Path);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("http://example.org/report.pdf")]
    [InlineData("https://example.org/report.pdf")]
    public async Task HttpSourcesKeepTheirUrl(string uri)
    {
        using var source = await QQMediaSource.ResolveAsync(new FileSegment(uri));
        Assert.Equal(uri, source.Url!.AbsoluteUri);
        Assert.Null(source.Path);
        Assert.Equal("report.pdf", source.FileName);
    }

    private sealed class MeasuringStream : MemoryStream
    {
        public int MaxWriteSize { get; private set; }
        public int WriteCount { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaxWriteSize = Math.Max(MaxWriteSize, buffer.Length);
            WriteCount++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
