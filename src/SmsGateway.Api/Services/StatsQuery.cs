using Microsoft.EntityFrameworkCore;
using SmsGateway.Api.Contracts;
using SmsGateway.Api.Domain;

namespace SmsGateway.Api.Services;

public static class StatsQuery
{
    public static async Task<SmsStatsResponse> ComputeAsync(IQueryable<SmsMessage> query, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await query
            .Where(m => m.CreatedAt >= from && m.CreatedAt < to)
            .GroupBy(m => m.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Of(SmsStatus s) => rows.FirstOrDefault(r => r.Key == s)?.Count ?? 0;

        return new SmsStatsResponse(from, to, rows.Sum(r => r.Count),
            Of(SmsStatus.Pending), Of(SmsStatus.Assigned), Of(SmsStatus.Sent),
            Of(SmsStatus.Delivered), Of(SmsStatus.Failed), Of(SmsStatus.Cancelled));
    }
}
