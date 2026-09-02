using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQ2Parser.Core.Upload;

/// <summary>One observed character in an attendance snapshot.</summary>
public sealed record AttendanceMember
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("first_seen")] public long FirstSeen { get; init; }
    [JsonPropertyName("last_seen")] public long LastSeen { get; init; }
}

/// <summary>
/// A cumulative raid-attendance snapshot for the EQ2Lexicon
/// /api/attendance/ingest endpoint (Phase 2 server work — until it exists a
/// 404 is expected and callers stay quiet). The client sends its WHOLE
/// current session state each time; the server's merge is idempotent.
/// Wire naming mirrors the parses ingest conventions (logger_name /
/// logger_server).
/// </summary>
public sealed record AttendancePayload
{
    [JsonPropertyName("logger_name")] public required string LoggerName { get; init; }
    [JsonPropertyName("logger_server")] public required string LoggerServer { get; init; }
    /// <summary>Optional — the server resolves and verifies the uploader's
    /// guild from logger_name anyway (same machinery as parse ingest).</summary>
    [JsonPropertyName("guild_name")] public string? GuildName { get; init; }
    [JsonPropertyName("sent_at")] public long SentAt { get; init; }
    [JsonPropertyName("raid_members")] public List<AttendanceMember> RaidMembers { get; init; } = [];
    [JsonPropertyName("online_guildies")] public List<AttendanceMember> OnlineGuildies { get; init; } = [];
    [JsonPropertyName("zones")] public List<string> Zones { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}
