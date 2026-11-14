using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackloggdLogger.Core;

public sealed class StateStore(string path)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new LenientEnumConverter() }
    };

    public bool RecoveredFromCorrupt { get; private set; }

    public bool LoadFailed { get; private set; }

    // Where an unreadable state file was moved, after a load that recovered from one.
    public string? BackupPath { get; private set; }

    public void MarkLoadFailed() => LoadFailed = true;

    string ReadWithRetry()
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Thread.Sleep(200);
            return File.ReadAllText(path);
        }
    }

    public AppState Load(DateTime? now = null)
    {
        var state = LoadFile();
        state.UpgradeJournal();
        state.PruneMissed(now ?? DateTime.Now);
        return state;
    }

    AppState LoadFile()
    {
        LoadFailed = false;
        if (!File.Exists(path)) return new AppState();
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(ReadWithRetry(), JsonOptions) ?? new AppState();
            var fileTime = File.GetLastWriteTime(path);
            foreach (var g in state.Games.Values) g.FillAddedAt(fileTime);
            return state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadFailed = true;
            return new AppState();
        }
        catch (JsonException)
        {
            var bad = Path.Combine(Path.GetDirectoryName(path)!, $"state.bad-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
            File.Move(path, bad);
            RecoveredFromCorrupt = true;
            BackupPath = bad;
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        var target = LoadFailed ? Path.Combine(Path.GetDirectoryName(path)!, "state.recovered.json") : path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = target + ".tmp";
        // Flushed to disk before the rename, so a power cut can't leave the new name on empty content.
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state, JsonOptions);
            file.Flush(flushToDisk: true);
        }
        File.Move(tmp, target, overwrite: true);
    }
}

// Enums are written as names and read from a name or number; an unknown value reads as the default, so one bad
// setting doesn't make the file unreadable. Nullable enums need their own converter to keep null as null.
sealed class LenientEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum || Nullable.GetUnderlyingType(typeToConvert)?.IsEnum == true;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        Nullable.GetUnderlyingType(typeToConvert) is { } inner
            ? (JsonConverter)Activator.CreateInstance(typeof(OfNullable<>).MakeGenericType(inner))!
            : (JsonConverter)Activator.CreateInstance(typeof(Of<>).MakeGenericType(typeToConvert))!;

    // A [Flags] enum takes any combination of its named bits ("Icon, Source"); others only a named value.
    static bool Valid<T>(T v) where T : struct, Enum
    {
        if (!typeof(T).IsDefined(typeof(FlagsAttribute), false)) return Enum.IsDefined(v);
        var mask = Enum.GetValues<T>().Aggregate(0L, (m, x) => m | Convert.ToInt64(x));
        return (Convert.ToInt64(v) & ~mask) == 0;
    }

    static T? Parse<T>(ref Utf8JsonReader reader) where T : struct, Enum
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var name = reader.GetString();
                return Enum.TryParse<T>(name, true, out var v) && !int.TryParse(name, out _) && Valid(v) ? v : null;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var n) && Valid((T)Enum.ToObject(typeof(T), n)) ? (T)Enum.ToObject(typeof(T), n) : null;
            default:
                reader.Skip();
                return null;
        }
    }

    // A plain enum reads null (and anything it lacks) as its default.
    sealed class Of<T> : JsonConverter<T> where T : struct, Enum
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse<T>(ref reader) ?? default;

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    // A nullable enum keeps null as null, and reads a value it lacks as null too.
    sealed class OfNullable<T> : JsonConverter<T?> where T : struct, Enum
    {
        public override bool HandleNull => true;

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : Parse<T>(ref reader);

        public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
        {
            if (value is { } v) writer.WriteStringValue(v.ToString());
            else writer.WriteNullValue();
        }
    }
}
