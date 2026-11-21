namespace BusinessLogicLayer.Models;

public enum BusinessErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unavailable
}

public sealed record BusinessError(BusinessErrorKind Kind, string? Reason = null);
