using Domic.Core.Domain.Contracts.Interfaces;
using MD.PersianDateTime.Standard;

namespace Domic.Core.Infrastructure.Concretes;

public class DomicDateTime : IDateTime
{
    private readonly TimeZoneInfo _tehranTimeZone;

    public string ToPersianShortDate(DateTime dateTime) => new PersianDateTime(dateTime).ToShortDateString();
    public string ToPersianShortDateTime(DateTime dateTime) => new PersianDateTime(dateTime).ToString("yyyy/MM/dd HH:mm:ss");
    public string ToPersianShortDateOnly(DateOnly dateOnly) => new PersianDateTime(dateOnly.ToDateTime(new TimeOnly(0, 0))).ToString("yyyy/MM/dd");
    public DateTime Now() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran"));
}