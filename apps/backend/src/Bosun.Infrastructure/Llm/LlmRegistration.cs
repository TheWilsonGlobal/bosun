using Bosun.Application;
using Bosun.Application.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bosun.Infrastructure.Llm;

public static class LlmRegistration
{
    /// <summary>
    /// ILlmClient / IEmbeddingClient (Gemini over typed-style named HttpClients with the resilience pipeline), wrapped by the
    /// logging + llm_calls recorder, plus <see cref="StructuredLlm"/> and the usage queries. Options bind lazily from the
    /// "Llm", "Embedding" and "Llm:Pricing" sections, and a missing key only fails at call time (llm_not_configured).
    /// Tests swap the network with <c>services.AddHttpClient(GeminiHttp.LlmClientName).ConfigurePrimaryHttpMessageHandler(...)</c>.
    /// </summary>
    public static IServiceCollection AddBosunLlm(this IServiceCollection services)
    {
        services.AddOptions<LlmOptions>().BindConfiguration("Llm");
        services.AddOptions<EmbeddingOptions>().BindConfiguration("Embedding");
        services.AddOptions<PricingOptions>().BindConfiguration("Llm:Pricing");

        services.AddSingleton<LlmPricing>();
        services.AddSingleton<ILlmCallLog, LlmCallRecorder>();

        services.AddGeminiClient(GeminiHttp.LlmClientName);
        services.AddGeminiClient(GeminiHttp.EmbeddingClientName);

        services.AddSingleton<ILlmClient>(sp => new RecordingLlmClient(
            new GeminiLlmClient(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<LlmOptions>>()),
            sp.GetRequiredService<ILlmCallLog>(),
            sp.GetRequiredService<LlmPricing>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RecordingLlmClient>>()));

        services.AddSingleton<IEmbeddingClient>(sp => new RecordingEmbeddingClient(
            new GeminiEmbeddingClient(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<EmbeddingOptions>>()),
            sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Provider,
            sp.GetRequiredService<ILlmCallLog>(),
            sp.GetRequiredService<LlmPricing>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RecordingEmbeddingClient>>()));

        services.AddSingleton<StructuredLlm>();
        services.AddScoped<LlmUsageQueries>();
        return services;
    }
}
