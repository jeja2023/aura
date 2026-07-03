using System.Globalization;
using System.Text.Json;
using Aura.Api.Logging;
using Aura.Api.Serialization;
using Microsoft.Extensions.Logging.Console;

namespace Aura.Api.Extensions;

public static class AuraHostExtensions
{
    public static WebApplicationBuilder ConfigureAuraHost(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddConsole(options => options.FormatterName = "pure");
        builder.Logging.AddConsoleFormatter<PureConsoleFormatter, ConsoleFormatterOptions>();

        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("zh-CN");

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            foreach (var converter in AuraJsonSerializerOptions.Default.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });

        builder.Services.AddOpenApi();
        return builder;
    }
}
