using DbRipple.Api.Middleware;

namespace DbRipple.Api.Tests;

/// <summary>교차 사이트 요청 방어(<see cref="CrossSiteRequestGuard"/>)를 통과하는 요청 표지 헤더를 모든 요청에 붙이는 시험용 도우미. POST를 보내는 시험은 이 도우미로 클라이언트를 만든다.</summary>
static class GuardedClient
{
    /// <summary>이 클라이언트의 모든 요청에 <c>X-DbRipple-Request: 1</c>을 붙인다(Origin·Sec-Fetch-Site는 붙이지 않는다: 브라우저 밖 도구와 같다).</summary>
    public static HttpClient WithGuardHeader(this HttpClient client)
    {
        client.DefaultRequestHeaders.Remove(CrossSiteRequestGuard.HeaderName);
        client.DefaultRequestHeaders.Add(CrossSiteRequestGuard.HeaderName, CrossSiteRequestGuard.HeaderValue);
        return client;
    }
}
