/* 文件：路由映射入口（聚合各域端点） | File: Endpoint mapping entry */
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapAuraEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration, bool isDev)
    {
        var ctx = new AuraEndpointContext(app, configuration, isDev);
        AuraEndpointsCore.Map(app, ctx);
        AuraEndpointsAuth.Map(app, ctx);
        AuraEndpointsCampusFloor.Map(app, ctx);
        AuraEndpointsDeviceCapture.Map(app, ctx);
        AuraEndpointsDomain.Map(app, ctx);
        return app;
    }
}
