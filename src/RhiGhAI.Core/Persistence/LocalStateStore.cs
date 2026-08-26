using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Events;

namespace RhiGhAI.Core.Persistence;

public sealed class LocalStateStore
{
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string MutexName = "Local\\RhiGhAI-State-v1";

    public LocalStateStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductInfo.Name);
    }

    public string RootDirectory { get; }

    private string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    private string ConversationsPath => Path.Combine(RootDirectory, "conversations.json");

    public RhiGhAISettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return RhiGhAISettings.Default;
            }

            return (JsonSerializer.Deserialize<RhiGhAISettings>(File.ReadAllText(SettingsPath), _options) ?? RhiGhAISettings.Default).Validate();
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            return RhiGhAISettings.Default;
        }
    }

    public void SaveSettings(RhiGhAISettings settings)
    {
        WithLock(() => WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings.Validate(), _options)));
    }

    public ConversationBinding? FindConversation(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(ConversationsPath))
        {
            return null;
        }

        try
        {
            return WithLock(() =>
            {
                Dictionary<string, ConversationBinding>? bindings = JsonSerializer.Deserialize<Dictionary<string, ConversationBinding>>(
                    File.ReadAllText(ConversationsPath),
                    _options);
                string key = DocumentKey(documentPath);
                return bindings is not null && bindings.TryGetValue(key, out ConversationBinding? binding) ? binding : null;
            });
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    public void SaveConversation(string documentPath, string threadId, Guid conversationId)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return;
        }

        WithLock(() =>
        {
            Directory.CreateDirectory(RootDirectory);
            Dictionary<string, ConversationBinding> bindings = LoadBindingsForWrite();
            bindings[DocumentKey(documentPath)] = new ConversationBinding(threadId, conversationId, DateTimeOffset.UtcNow);
            WriteAtomic(ConversationsPath, JsonSerializer.Serialize(bindings, _options));
        });
    }

    public void ClearThread(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(ConversationsPath))
        {
            return;
        }

        WithLock(() =>
        {
            Dictionary<string, ConversationBinding> bindings = LoadBindingsForWrite();
            if (bindings.Remove(DocumentKey(documentPath)))
            {
                WriteAtomic(ConversationsPath, JsonSerializer.Serialize(bindings, _options));
            }
        });
    }

    public void AppendEvent(EventEnvelope envelope)
    {
        WithLock(() =>
        {
            string directory = Path.Combine(RootDirectory, "Transcripts");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{envelope.ConversationId:D}.jsonl");
            string json = JsonSerializer.Serialize(envelope, _options);
            using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.WriteLine(json.ReplaceLineEndings(" "));
            writer.Flush();
            stream.Flush(true);
        });
    }

    public IReadOnlyList<EventEnvelope> LoadEvents(Guid conversationId, int maxCount = 500)
    {
        string path = Path.Combine(RootDirectory, "Transcripts", $"{conversationId:D}.jsonl");
        if (!File.Exists(path))
        {
            return Array.Empty<EventEnvelope>();
        }

        return WithLock<IReadOnlyList<EventEnvelope>>(() =>
        {
            List<EventEnvelope> events = [];
            foreach (string line in File.ReadLines(path))
            {
                try
                {
                    EventEnvelope? envelope = JsonSerializer.Deserialize<EventEnvelope>(line, _options);
                    if (envelope is not null)
                    {
                        events.Add(envelope);
                    }
                }
                catch (JsonException)
                {
                    break;
                }
            }

            return events.TakeLast(Math.Clamp(maxCount, 1, 2000)).ToArray();
        });
    }

    public static string DocumentKeyForPath(string documentPath, uint runtimeSerial) =>
        string.IsNullOrWhiteSpace(documentPath) ? $"unsaved:{runtimeSerial}" : DocumentKey(documentPath);

    private static string DocumentKey(string path)
    {
        string canonical = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private Dictionary<string, ConversationBinding> LoadBindingsForWrite()
    {
        if (!File.Exists(ConversationsPath))
        {
            return new Dictionary<string, ConversationBinding>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ConversationBinding>>(File.ReadAllText(ConversationsPath), _options) ?? new();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            string quarantineDirectory = Path.Combine(RootDirectory, "Quarantine");
            Directory.CreateDirectory(quarantineDirectory);
            string quarantinePath = Path.Combine(quarantineDirectory, $"conversations-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
            File.Move(ConversationsPath, quarantinePath, false);
            return new Dictionary<string, ConversationBinding>();
        }
    }

    private static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, contents, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Move(temporary, path, true);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static void WithLock(Action action) => WithLock(() =>
    {
        action();
        return true;
    });

    private static T WithLock<T>(Func<T> action)
    {
        using Mutex mutex = new(false, MutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            throw new IOException("RhiGhAI local state is busy in another Rhino process.");
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}

public sealed record ConversationBinding(string ThreadId, Guid ConversationId, DateTimeOffset UpdatedAt);
