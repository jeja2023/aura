using Aura.Api.Services.Hikvision;
using Microsoft.Extensions.Options;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddAuraHikvisionServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HikvisionIsapiOptions>()
            .Bind(configuration.GetSection(HikvisionIsapiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HikvisionIsapiOptions>, HikvisionIsapiOptionsValidator>();
        services.PostConfigure<HikvisionIsapiOptions>(o =>
        {
            static string? ReadEnv(string? variableName)
            {
                if (string.IsNullOrWhiteSpace(variableName))
                {
                    return null;
                }

                return Environment.GetEnvironmentVariable(variableName.Trim());
            }

            if (string.IsNullOrWhiteSpace(o.DefaultUserName))
            {
                var v = ReadEnv(o.DefaultUserNameEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(v))
                {
                    o.DefaultUserName = v.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(o.DefaultPassword))
            {
                var v = ReadEnv(o.DefaultPasswordEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(v))
                {
                    o.DefaultPassword = v;
                }
            }
        });

        services.AddHttpContextAccessor();
        services.AddSingleton<HikvisionIsapiClient>();
        services.AddSingleton<HikvisionAlertStreamRegistry>();
        services.AddScoped<HikvisionNvrIntegrationService>();
        services.AddScoped<HikvisionIsapiGatewayService>();
    }
}
