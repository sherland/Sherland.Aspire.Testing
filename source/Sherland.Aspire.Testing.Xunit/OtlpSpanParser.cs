using System.Text.Json;

namespace Sherland.Aspire.Testing.Xunit;

/// <summary>
/// A captured span extracted from an OTLP JSON export (either the browser's own export,
/// or the Aspire dashboard's REST telemetry API, which shares the same wire shape).
/// </summary>
public sealed record CapturedSpan(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string ServiceName,
    string Scope,
    DateTimeOffset StartTime,
    bool IsError,
    string? StatusMessage,
    IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>True when the span has no parent (it is a trace root).</summary>
    public bool IsRoot => string.IsNullOrEmpty(ParentSpanId);
}

/// <summary>
/// Parses the OTLP JSON <c>resourceSpans -&gt; scopeSpans -&gt; spans</c> shape into
/// <see cref="CapturedSpan"/> records. Shared by <see cref="OtelTraceCapture"/>'s live
/// dashboard polling and its failure-diagnostics span counting.
/// </summary>
internal static class OtlpSpanParser
{
    /// <summary>
    /// Parses an OTLP export document (an object with a top-level <c>resourceSpans</c> array),
    /// e.g. a raw browser OTLP export payload.
    /// </summary>
    internal static List<CapturedSpan> ParseSpansDocument(JsonElement root)
    {
        if (!root.TryGetProperty("resourceSpans", out var resourceSpans))
            return [];

        return ParseResourceSpans(resourceSpans);
    }

    /// <summary>
    /// Parses a <c>resourceSpans</c> JSON array element directly, as returned by the
    /// Aspire dashboard's <c>/api/telemetry/spans</c> REST endpoint under its <c>data</c> property.
    /// </summary>
    internal static List<CapturedSpan> ParseResourceSpans(JsonElement resourceSpansElement)
    {
        var result = new List<CapturedSpan>();

        if (resourceSpansElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var rs in resourceSpansElement.EnumerateArray())
        {
            var serviceName = ReadStringAttribute(rs, "resource", "service.name");

            if (!rs.TryGetProperty("scopeSpans", out var scopeSpans))
                continue;

            foreach (var ss in scopeSpans.EnumerateArray())
            {
                var scope = ss.TryGetProperty("scope", out var scopeEl) &&
                            scopeEl.TryGetProperty("name", out var scopeName)
                    ? scopeName.GetString() ?? ""
                    : "";

                if (!ss.TryGetProperty("spans", out var spanArray))
                    continue;

                foreach (var s in spanArray.EnumerateArray())
                {
                    var traceId = s.GetProperty("traceId").GetString() ?? "";
                    var spanId = s.GetProperty("spanId").GetString() ?? "";
                    var parentSpanId = s.TryGetProperty("parentSpanId", out var pid)
                        ? pid.GetString()
                        : null;
                    var name = s.GetProperty("name").GetString() ?? "";
                    var attrs = ReadSpanAttributes(s);
                    var startTime = ParseUnixNanoToDateTimeOffset(
                        s.TryGetProperty("startTimeUnixNano", out var st) ? st.GetString() : null);
                    var (isError, statusMessage) = ReadStatus(s);

                    result.Add(new CapturedSpan(
                        traceId, spanId, parentSpanId, name, serviceName, scope,
                        startTime, isError, statusMessage, attrs));
                }
            }
        }

        return result;
    }

    private static (bool IsError, string? Message) ReadStatus(JsonElement span)
    {
        if (!span.TryGetProperty("status", out var status))
            return (false, null);

        // OTLP status.code: UNSET=0, OK=1, ERROR=2
        var isError = status.TryGetProperty("code", out var code) && code.GetInt32() == 2;
        var message = status.TryGetProperty("message", out var msg) ? msg.GetString() : null;
        return (isError, message);
    }

    private static DateTimeOffset ParseUnixNanoToDateTimeOffset(string? unixNano)
    {
        if (unixNano is null || !long.TryParse(unixNano, out var nano))
            return default;

        // DateTimeOffset ticks are 100ns units — finer than millisecond precision.
        // Truncating to milliseconds here (e.g. via FromUnixTimeMilliseconds(nano / 1_000_000))
        // can round a span's start time *down* past a window boundary set moments earlier,
        // silently dropping fast round-trip spans (a request completing in single-digit
        // milliseconds) from OtelTraceCapture's Reset()-based window filter.
        return DateTimeOffset.UnixEpoch.AddTicks(nano / 100L);
    }

    private static string ReadStringAttribute(JsonElement parent, string propertyName, string key)
    {
        if (!parent.TryGetProperty(propertyName, out var prop) ||
            !prop.TryGetProperty("attributes", out var attrs))
            return "";

        foreach (var attr in attrs.EnumerateArray())
        {
            if (attr.GetProperty("key").GetString() == key &&
                attr.TryGetProperty("value", out var val) &&
                val.TryGetProperty("stringValue", out var sv))
                return sv.GetString() ?? "";
        }

        return "";
    }

    private static Dictionary<string, string> ReadSpanAttributes(JsonElement span)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!span.TryGetProperty("attributes", out var attrs))
            return dict;

        foreach (var attr in attrs.EnumerateArray())
        {
            var key = attr.GetProperty("key").GetString() ?? "";
            if (!attr.TryGetProperty("value", out var val)) continue;

            string? strVal = null;
            if (val.TryGetProperty("stringValue", out var sv)) strVal = sv.GetString();
            else if (val.TryGetProperty("intValue", out var iv)) strVal = iv.GetRawText();
            else if (val.TryGetProperty("boolValue", out var bv)) strVal = bv.GetRawText();
            else if (val.TryGetProperty("doubleValue", out var dv)) strVal = dv.GetRawText();

            if (strVal is not null)
                dict[key] = strVal;
        }

        return dict;
    }
}
