using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApi.Models.Enrollments;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class CreateEnrollmentRequest
{
    [Required]
    public long? ClassId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; set; }
}
