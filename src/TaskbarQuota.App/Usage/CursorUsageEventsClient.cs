using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarQuota.Usage
{
    internal readonly record struct CursorDashboardUsageEvent(
        DateTimeOffset Timestamp,
        string Model,
        TokenBreakdown Tokens,
        double? ReportedCostUsd,
        double? MeteredCostUsd,
        string? SessionId,
        string Signature);

    internal static class CursorUsageEventsClient
    {
        private const string Endpoint = "https://cursor.com/api/dashboard/get-filtered-usage-events";
        private const int PageSize = 1000;
        private const int MaxPages = 200;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private sealed record Page(int? TotalCount, IReadOnlyList<CursorDashboardUsageEvent> Events);

        public static async Task<UsageHistory> FetchHistoryAsync(
            string cookieHeader,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            HttpClient? client = null)
        {
            var pages = new List<IReadOnlyList<CursorDashboardUsageEvent>>();
            int? expectedTotal = null;
            var complete = false;
            var start = new DateTimeOffset(now.LocalDateTime.Date.AddDays(-89), now.Offset).ToUnixTimeMilliseconds();
            var end = now.ToUnixTimeMilliseconds();

            for (var pageNumber = 1; pageNumber <= MaxPages; pageNumber++)
            {
                var page = await FetchPageAsync(client ?? Http, cookieHeader, pageNumber, start, end, cancellationToken)
                    .ConfigureAwait(false);
                if (page.TotalCount is { } total)
                {
                    if (total < 0 || expectedTotal is { } previous && previous != total)
                        throw new InvalidDataException("Cursor usage event count changed during pagination.");
                    expectedTotal = total;
                }

                if (page.Events.Count == 0)
                {
                    complete = true;
                    break;
                }

                pages.Add(page.Events);
                if (page.Events.Count < PageSize)
                {
                    complete = true;
                    break;
                }
            }

            if (!complete)
                throw new InvalidDataException("Cursor usage history exceeded the pagination safety limit.");

            var rawCount = pages.Sum(page => page.Count);
            if (expectedTotal is null)
            {
                if (rawCount != 0)
                    throw new InvalidDataException("Cursor omitted the authoritative usage event count.");
                return UsageHistoryService.BuildFromCursorDashboardEvents(Array.Empty<CursorDashboardUsageEvent>(), now);
            }
            if (rawCount < expectedTotal.Value)
                throw new InvalidDataException("Cursor returned a partial usage history.");

            var events = ReconcileBoundaryOverlap(pages, expectedTotal.Value);
            return UsageHistoryService.BuildFromCursorDashboardEvents(events, now);
        }

        private static async Task<Page> FetchPageAsync(
            HttpClient client,
            string cookieHeader,
            int page,
            long start,
            long end,
            CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new
            {
                page,
                pageSize = PageSize,
                startDate = start.ToString(CultureInfo.InvariantCulture),
                endDate = end.ToString(CultureInfo.InvariantCulture),
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Cursor cookies expired. Sign in again.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Cursor usage events returned {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParsePage(document.RootElement);
        }

        private static Page ParsePage(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Cursor usage event response was not an object.");
            if (!root.EnumerateObject().Any())
                return new Page(0, Array.Empty<CursorDashboardUsageEvent>());

            int? total = null;
            if (root.TryGetProperty("totalUsageEventsCount", out var countNode))
            {
                var count = ReadNonNegativeUInt64(countNode, "totalUsageEventsCount");
                if (count > int.MaxValue) throw new InvalidDataException("Cursor usage event count is too large.");
                total = (int)count;
            }

            if (!root.TryGetProperty("usageEventsDisplay", out var eventsNode))
            {
                if (total.HasValue && root.EnumerateObject().Count() == 1)
                    return new Page(total, Array.Empty<CursorDashboardUsageEvent>());
                throw new InvalidDataException("Cursor usage event response omitted its event array.");
            }
            if (eventsNode.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Cursor usage event array was malformed.");

            var events = new List<CursorDashboardUsageEvent>();
            foreach (var eventNode in eventsNode.EnumerateArray())
            {
                if (eventNode.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Cursor usage event was malformed.");
                var timestampMs = ReadRequiredInt64(eventNode, "timestamp");
                if (timestampMs <= 0) throw new InvalidDataException("Cursor usage event timestamp was invalid.");
                var hasTokenUsage = eventNode.TryGetProperty("tokenUsage", out var tokenNode)
                    && tokenNode.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
                if (hasTokenUsage && tokenNode.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Cursor token usage was malformed.");

                var tokens = hasTokenUsage ? new TokenBreakdown
                {
                    Input = ReadOptionalToken(tokenNode, "inputTokens"),
                    Output = ReadOptionalToken(tokenNode, "outputTokens"),
                    CacheWrite5m = ReadOptionalToken(tokenNode, "cacheWriteTokens"),
                    CacheRead = ReadOptionalToken(tokenNode, "cacheReadTokens"),
                } : new TokenBreakdown();

                var model = ReadOptionalString(eventNode, "model") ?? "cursor-unknown";
                double? reportedCost = hasTokenUsage && ReadOptionalNonNegativeDouble(tokenNode, "totalCents") is { } cents
                    ? cents / 100d
                    : null;
                double? meteredCost = ReadOptionalNonNegativeDouble(eventNode, "chargedCents") is { } charged
                    ? charged / 100d
                    : null;
                var session = ReadOptionalString(eventNode, "conversationId")
                    ?? ReadOptionalString(eventNode, "composerId")
                    ?? ReadOptionalString(eventNode, "sessionId");
                events.Add(new CursorDashboardUsageEvent(
                    DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                    model,
                    tokens,
                    reportedCost,
                    meteredCost,
                    session,
                    eventNode.GetRawText()));
            }
            return new Page(total, events);
        }

        internal static IReadOnlyList<CursorDashboardUsageEvent> ParsePageForTesting(string json, out int? total)
        {
            using var document = JsonDocument.Parse(json);
            var page = ParsePage(document.RootElement);
            total = page.TotalCount;
            return page.Events;
        }

        internal static IReadOnlyList<CursorDashboardUsageEvent> ReconcileBoundaryOverlap(
            IReadOnlyList<IReadOnlyList<CursorDashboardUsageEvent>> pages,
            int expectedTotal)
        {
            var rawCount = pages.Sum(page => page.Count);
            if (rawCount < expectedTotal)
                throw new InvalidDataException("Cursor returned fewer usage events than advertised.");
            if (pages.Count == 0)
                return expectedTotal == 0 ? Array.Empty<CursorDashboardUsageEvent>()
                    : throw new InvalidDataException("Cursor returned no usage events.");

            var removals = rawCount - expectedTotal;
            var result = new List<CursorDashboardUsageEvent>(pages[0]);
            for (var index = 1; index < pages.Count; index++)
            {
                var previous = pages[index - 1];
                var current = pages[index];
                var overlap = BoundaryOverlap(previous, current);
                var remove = Math.Min(overlap, removals);
                result.AddRange(current.Skip(remove));
                removals -= remove;
            }
            if (removals != 0 || result.Count != expectedTotal)
                throw new InvalidDataException("Cursor pagination contained an unexplained duplicate or count mismatch.");
            return result;
        }

        private static int BoundaryOverlap(
            IReadOnlyList<CursorDashboardUsageEvent> previous,
            IReadOnlyList<CursorDashboardUsageEvent> current)
        {
            for (var count = Math.Min(previous.Count, current.Count); count > 0; count--)
            {
                var matches = true;
                for (var index = 0; index < count; index++)
                    if (!string.Equals(previous[previous.Count - count + index].Signature,
                            current[index].Signature, StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                if (matches) return count;
            }
            return 0;
        }

        private static ulong ReadOptionalToken(JsonElement parent, string name)
            => parent.TryGetProperty(name, out var node) && node.ValueKind is not JsonValueKind.Null
                ? ReadNonNegativeUInt64(node, name)
                : 0;

        private static ulong ReadNonNegativeUInt64(JsonElement node, string name)
        {
            if (node.ValueKind == JsonValueKind.Number && node.TryGetUInt64(out var number)) return number;
            if (node.ValueKind == JsonValueKind.String
                && ulong.TryParse(node.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
            throw new InvalidDataException($"Cursor {name} was not a non-negative integer.");
        }

        private static long ReadRequiredInt64(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var node))
                throw new InvalidDataException($"Cursor usage event omitted {name}.");
            if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var number)) return number;
            if (node.ValueKind == JsonValueKind.String
                && long.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
            throw new InvalidDataException($"Cursor {name} was not an integer.");
        }

        private static double? ReadOptionalNonNegativeDouble(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null)
                return null;
            double value;
            if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out value)
                || node.ValueKind == JsonValueKind.String
                    && double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                if (double.IsFinite(value) && value >= 0) return value;
            }
            throw new InvalidDataException($"Cursor {name} was invalid.");
        }

        private static string? ReadOptionalString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var node)) return null;
            return node.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(node.GetString()) ? null : node.GetString(),
                JsonValueKind.Number => node.GetRawText(),
                _ => null,
            };
        }
    }
}
