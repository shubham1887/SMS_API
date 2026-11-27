using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SmsGateway.Api.Data;

public class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v == null ? null : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
    v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
