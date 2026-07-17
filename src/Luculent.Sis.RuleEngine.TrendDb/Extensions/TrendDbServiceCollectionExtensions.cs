using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.TrendDb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class TrendDbServiceCollectionExtensions
{
    /// <summary>
    /// 注册 TrendDB 服务：TrendDbConnectionPool（Singleton）+ ITrendDataReader → TrendDbRealReader。
    /// 优先读取 "TrendDb" 配置节，fallback 到 TRENDDB_CONNECTION 环境变量。
    /// </summary>
    public static IServiceCollection AddTrendDb(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TrendDbOptions>(configuration.GetSection(TrendDbOptions.SectionName));

        services.PostConfigure<TrendDbOptions>(options =>
        {
            if (string.IsNullOrEmpty(options.ConnectionString))
                options.ConnectionString = configuration.GetValue<string>("TRENDDB_CONNECTION") ?? "";
        });

        services.AddSingleton<TrendDbConnectionPool>();
        services.AddSingleton<ITrendDataReader>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TrendDbOptions>>().Value;
            if (string.IsNullOrEmpty(opts.ConnectionString))
                throw new InvalidOperationException(
                    "TrendDB 连接字符串未配置，请设置 TrendDb:ConnectionString 或 TRENDDB_CONNECTION 环境变量");

            return new TrendDbRealReader(
                sp.GetRequiredService<TrendDbConnectionPool>(),
                sp.GetRequiredService<ILogger<TrendDbRealReader>>());
        });

        return services;
    }
}
