using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Providers;

namespace RhiGhAI.Core.Persistence;

public sealed record RhiGhAISettings(
    int SchemaVersion,
    int RetryMax,
    int TimeoutSeconds,
    TargetHost DefaultTarget,
    string? ModelId,
    string? EffortId,
    ProviderKind Provider = ProviderKind.Codex,
    string? Endpoint = null)
{
    public static RhiGhAISettings Default { get; } = new(1, 3, 180, TargetHost.Rhino, null, null);

    public RhiGhAISettings Validate()
    {
        if (SchemaVersion != 1 || RetryMax is < 1 or > 5 || TimeoutSeconds is < 30 or > 600)
        {
            throw new InvalidDataException("Настройки RhiGhAI выходят за разрешённые границы.");
        }

        if (Provider == ProviderKind.OpenAiCompatible && !string.IsNullOrWhiteSpace(Endpoint))
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https"))
            {
                throw new InvalidDataException("Адрес API должен быть абсолютным http(s) адресом.");
            }

            if (endpoint.Scheme == "http" && !endpoint.IsLoopback)
            {
                // The API key travels on every request to this address.
                throw new InvalidDataException("http допустим только для localhost; для внешнего адреса укажите https.");
            }
        }

        return this;
    }
}
