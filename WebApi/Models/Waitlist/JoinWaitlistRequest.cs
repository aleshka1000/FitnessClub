using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApi.Models.Waitlist;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class JoinWaitlistRequest
{
    [Required]
    public long? ClassId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; set; }
}
