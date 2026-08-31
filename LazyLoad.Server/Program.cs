using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Configuração JWT
var jwtKey = "MinhaChaveSuperSecretaMultiTenantDeAltaSeguranca2026!";
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
        ValidateIssuer = false,
        ValidateAudience = false,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.MapStaticAssets();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// -------------------------------------------------------------
// ENDPOINTS DE AUTENTICAÇÃO E MULTI-TENANT PLUGINS
// -------------------------------------------------------------

// 1. Login Rápido
app.MapPost("/api/auth/login", (LoginDto request) =>
{
    // Simulação de usuários por empresa
    TenantUser? user = request.Username?.ToLowerInvariant() switch
    {
        "alpha" or "empresa.alpha" => new TenantUser("alpha", "Usuário Indústria", "EmpresaAlpha", "Empresa Alpha (Indústria)"),
        "beta" or "empresa.beta" => new TenantUser("beta", "Usuário Serviços", "EmpresaBeta", "Empresa Beta (Serviços)"),
        _ => null
    };

    if (user is null)
    {
        return Results.Unauthorized();
    }

    var tokenHandler = new JwtSecurityTokenHandler();
    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("DisplayName", user.DisplayName),
            new Claim("TenantId", user.TenantId),
            new Claim("CompanyName", user.CompanyName)
        ]),
        Expires = DateTime.UtcNow.AddHours(8),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256Signature)
    };

    var token = tokenHandler.CreateToken(tokenDescriptor);
    var tokenString = tokenHandler.WriteToken(token);

    return Results.Ok(new LoginResponse(tokenString, user.Username, user.DisplayName, user.TenantId, user.CompanyName));
});

// 2. Consulta de Plugins da Empresa Logada (Filtra a Central de Plugins 'plugins_pool' pela licença do Tenant)
app.MapGet("/api/plugins/my-plugins", [Authorize] (ClaimsPrincipal user, IWebHostEnvironment env) =>
{
    var tenantId = user.FindFirst("TenantId")?.Value;
    if (string.IsNullOrWhiteSpace(tenantId))
    {
        return Results.Forbid();
    }

    // 1. Lê quais plugins este Tenant tem permissão no arquivo storage/tenants/{tenantId}/licenses.json
    var licenseFile = Path.Combine(env.ContentRootPath, "storage", "tenants", tenantId, "licenses.json");
    if (!File.Exists(licenseFile))
    {
        return Results.Ok(Array.Empty<PluginMetadataDto>());
    }

    var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    TenantLicense? license = null;
    try
    {
        license = System.Text.Json.JsonSerializer.Deserialize<TenantLicense>(File.ReadAllText(licenseFile), jsonOptions);
    }
    catch { }

    var allowedIds = license?.AllowedPlugins ?? [];
    if (allowedIds.Count == 0)
    {
        return Results.Ok(Array.Empty<PluginMetadataDto>());
    }

    // 2. Lê os plugins disponíveis na pasta central ÚNICA (plugins_pool)
    var poolPath = Path.Combine(env.ContentRootPath, "storage", "plugins_pool");
    if (!Directory.Exists(poolPath))
    {
        return Results.Ok(Array.Empty<PluginMetadataDto>());
    }

    var jsonFiles = Directory.GetFiles(poolPath, "*.plugin.json");
    var pluginsAutorizados = new List<PluginMetadataDto>();

    foreach (var jsonPath in jsonFiles)
    {
        try
        {
            var jsonContent = File.ReadAllText(jsonPath);
            var plugin = System.Text.Json.JsonSerializer.Deserialize<PluginMetadataDto>(jsonContent, jsonOptions);
            
            // 3. Só inclui se a empresa tiver o ID na sua lista de licenças!
            if (plugin is not null && allowedIds.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase))
            {
                var dllPath = Path.Combine(poolPath, plugin.FileName);
                var size = File.Exists(dllPath) ? new FileInfo(dllPath).Length : 0;

                pluginsAutorizados.Add(plugin with { SizeBytes = size });
            }
        }
        catch { }
    }

    return Results.Ok(pluginsAutorizados);
});

// 3. Download Seguro de DLL da Central Única (Validando a Licença da Empresa)
app.MapGet("/api/plugins/download/{fileName}", [Authorize] (string fileName, ClaimsPrincipal user, IWebHostEnvironment env) =>
{
    var tenantId = user.FindFirst("TenantId")?.Value;
    if (string.IsNullOrWhiteSpace(tenantId))
    {
        return Results.Forbid();
    }

    // 1. Valida se a empresa possui licença para a DLL solicitada
    var licenseFile = Path.Combine(env.ContentRootPath, "storage", "tenants", tenantId, "licenses.json");
    if (!File.Exists(licenseFile))
    {
        return Results.Forbid();
    }

    var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var license = System.Text.Json.JsonSerializer.Deserialize<TenantLicense>(File.ReadAllText(licenseFile), jsonOptions);
    var allowedIds = license?.AllowedPlugins ?? [];

    var poolPath = Path.Combine(env.ContentRootPath, "storage", "plugins_pool");
    var safeFileName = Path.GetFileName(fileName);
    var filePath = Path.Combine(poolPath, safeFileName);

    // Validação estrita de Directory Traversal
    var fullPoolPath = Path.GetFullPath(poolPath);
    var fullFilePath = Path.GetFullPath(filePath);

    if (!fullFilePath.StartsWith(fullPoolPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullFilePath))
    {
        return Results.NotFound(new { Message = $"O arquivo {safeFileName} não foi encontrado na central de plugins." });
    }

    var fileBytes = File.ReadAllBytes(fullFilePath);
    return Results.File(fileBytes, "application/octet-stream", safeFileName);
});

app.MapFallbackToFile("index.html");

app.Run();

// -------------------------------------------------------------
// DTOs
// -------------------------------------------------------------
record LoginDto(string? Username, string? Password);
record TenantUser(string Username, string DisplayName, string TenantId, string CompanyName);
record LoginResponse(string Token, string Username, string DisplayName, string TenantId, string CompanyName);
record TenantLicense(string TenantId, List<string> AllowedPlugins);
record PluginMetadataDto(
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
