using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

namespace LazyLoad.Services;

public class TenantPluginManager
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;
    private readonly Dictionary<string, Assembly> _loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    public List<PluginMetadataDto> AvailablePlugins { get; private set; } = [];
    public event Action? OnCatalogUpdated;

    public TenantPluginManager(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
        _auth.OnAuthStateChanged += async () =>
        {
            if (_auth.IsAuthenticated)
            {
                await RefreshCatalogAsync();
            }
            else
            {
                AvailablePlugins.Clear();
                OnCatalogUpdated?.Invoke();
            }
        };
    }

    public async Task RefreshCatalogAsync()
    {
        if (!_auth.IsAuthenticated || string.IsNullOrEmpty(_auth.Token))
        {
            AvailablePlugins.Clear();
            OnCatalogUpdated?.Invoke();
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "api/plugins/my-plugins");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                AvailablePlugins = await response.Content.ReadFromJsonAsync<List<PluginMetadataDto>>() ?? [];
            }
            else
            {
                AvailablePlugins.Clear();
            }
        }
        catch
        {
            AvailablePlugins.Clear();
        }

        OnCatalogUpdated?.Invoke();
    }

    public bool IsPluginLoaded(string pluginId) =>
        _loadedAssemblies.ContainsKey(pluginId);

    public Assembly? GetLoadedAssembly(string pluginId) =>
        _loadedAssemblies.TryGetValue(pluginId, out var asm) ? asm : null;

    public async Task<Assembly?> LoadPluginAssemblyAsync(PluginMetadataDto plugin, CancellationToken cancellationToken = default)
    {
        // Se já estiver na memória da sessão atual, reutiliza a instância carregada
        if (_loadedAssemblies.TryGetValue(plugin.Id, out var existingAssembly))
        {
            return existingAssembly;
        }

        if (!_auth.IsAuthenticated || string.IsNullOrEmpty(_auth.Token))
        {
            throw new UnauthorizedAccessException("Usuário não autenticado.");
        }

        // Baixa a DLL estritamente através do endpoint seguro da API do tenant
        var request = new HttpRequestMessage(HttpMethod.Get, $"api/plugins/download/{plugin.FileName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);

        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Não foi possível baixar a DLL do plugin ({response.StatusCode}).");
        }

        var assemblyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        
        // Injeta os bytes da DLL diretamente na memória RAM do WebAssembly
        var loadedAssembly = Assembly.Load(assemblyBytes);
        _loadedAssemblies[plugin.Id] = loadedAssembly;
        
        return loadedAssembly;
    }
}
