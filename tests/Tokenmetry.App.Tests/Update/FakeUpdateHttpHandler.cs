using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tokenmetry.App.Tests.Update;

/// <summary>更新检查/下载共用的假 HTTP handler：按请求 URL 定制响应，记录请求序列（镜像回退断言用）。</summary>
internal sealed class FakeUpdateHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<string> RequestedUrls { get; } = new();

    public FakeUpdateHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUrls.Add(request.RequestUri?.ToString() ?? "");
        return Task.FromResult(_responder(request));
    }

    /// <summary>releases/latest 的 302（网页端点契约：Location 相对路径即带 tag）。</summary>
    public static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
    };

    public static HttpResponseMessage Bytes(byte[] content, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        Content = new ByteArrayContent(content),
    };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code)
    {
        Content = new StringContent(""),
    };

    public static HttpResponseMessage Empty() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(""),
    };
}
