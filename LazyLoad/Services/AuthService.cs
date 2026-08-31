using System.Net.Http.Json;

namespace LazyLoad.Services;

public class AuthService
{
    private readonly HttpClient _http;

    public LoginResponse? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser != null;
    public string? Token => CurrentUser?.Token;

    public event Action? OnAuthStateChanged;

    public AuthService(HttpClient http)
    {
        _http = http;
    }

    public async Task<bool> LoginAsync(string username, string password = "123")
    {
        try
        {
            var response = await _http.PostAsJsonAsync("api/auth/login", new { Username = username, Password = password });
            if (response.IsSuccessStatusCode)
            {
                CurrentUser = await response.Content.ReadFromJsonAsync<LoginResponse>();
                OnAuthStateChanged?.Invoke();
                return true;
            }
        }
        catch
        {
            // Tratamento de falha de conexão com a API
        }

        CurrentUser = null;
        OnAuthStateChanged?.Invoke();
        return false;
    }

    public void Logout()
    {
        CurrentUser = null;
        OnAuthStateChanged?.Invoke();
    }
}
