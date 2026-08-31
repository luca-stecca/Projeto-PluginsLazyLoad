namespace LazyLoad.Services;

public record LoginResponse(
    string Token,
    string Username,
    string DisplayName,
    string TenantId,
    string CompanyName
);

public record PluginMetadataDto(
    string Id,
    string Title,
    string FileName,
    string RoutePrefix,
    string Icon,
    string Description,
    string ButtonComponentType,
    string ButtonLabel,
    long SizeBytes
);
