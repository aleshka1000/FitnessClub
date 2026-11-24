using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.ModelBinding;

public sealed class QueryDateModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var input = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);

        if (input == ValueProviderResult.None)
            return Task.CompletedTask;

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, input);
        var type = Nullable.GetUnderlyingType(bindingContext.ModelType) ?? bindingContext.ModelType;

        if (type == typeof(DateOnly) && TryDate(input.FirstValue, out var date))
            bindingContext.Result = ModelBindingResult.Success(date);
        else if (type == typeof(DateTime) && TryInstant(input.FirstValue, out var instant))
            bindingContext.Result = ModelBindingResult.Success(instant);
        else
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "Invalid date value.");

        return Task.CompletedTask;
    }

    private static bool TryInstant(string? text, out DateTime value)
    {
        value = default;

        if (text is null || !Regex.IsMatch(text,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})?$"))
            return false;

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
            return false;

        value = instant.UtcDateTime;

        return true;
    }

    private static bool TryDate(string? text, out DateOnly value) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
