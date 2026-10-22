using System.ComponentModel.DataAnnotations;

namespace WebApplication1.Domain;

public record CreateTaskRequest(
    [property: Required(AllowEmptyStrings = false, ErrorMessage = "Title is required.")]
    [property: StringLength(200, MinimumLength = 1, ErrorMessage = "Title must be at most 200 characters.")]
    [property: RegularExpression(@"^\s*\S[\s\S]*$", ErrorMessage = "Title must not be whitespace only.")]
    string? Title,

    [property: EnumDataType(typeof(Priority), ErrorMessage = "Priority must be Low, Medium or High.")]
    Priority? Priority = null);
