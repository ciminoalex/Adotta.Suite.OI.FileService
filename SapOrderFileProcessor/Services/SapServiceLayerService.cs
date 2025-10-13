using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using SapOrderFileProcessor.Models;

namespace SapOrderFileProcessor.Services;

public sealed class SapServiceLayerService : ISapServiceLayerService
{
	private readonly HttpClient _httpClient;
	private readonly ILogger<SapServiceLayerService> _logger;
	private readonly SapConfiguration _config;
	private readonly AsyncRetryPolicy<HttpResponseMessage> _httpRetryPolicy;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	public SapServiceLayerService(HttpClient httpClient, ILogger<SapServiceLayerService> logger, IOptions<SapConfiguration> options)
	{
		_httpClient = httpClient;
		_logger = logger;
		_config = options.Value;

		_httpRetryPolicy = Policy<HttpResponseMessage>
			.Handle<HttpRequestException>()
			.OrResult(r => (int)r.StatusCode is >= 500 or 408)
			.WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, retryAttempt))),
				(outcome, timespan, attempt, _) =>
				{
					_logger.LogWarning("Retry HTTP attempt {Attempt} after {Delay}s. Status={StatusCode}", attempt, timespan.TotalSeconds, outcome.Result?.StatusCode);
				});
	}

    public async Task<SapSession> LoginAsync()
	{
		var loginBody = new
		{
			CompanyDB = _config.CompanyDB,
			UserName = _config.UserName,
			Password = _config.Password
		};

        _logger.LogInformation("Login a SAP Service Layer {Url}", _httpClient.BaseAddress);
        using var response = await _httpRetryPolicy.ExecuteAsync(() =>
        {
            var json = JsonSerializer.Serialize(loginBody, JsonOptions);
            var req = new HttpRequestMessage(HttpMethod.Post, "Login")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            // Some SL setups require ROUTEID (and sometimes B1SESSION) even on Login for stickiness
            var cookieBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(_config.PreLoginB1SessionId))
            {
                cookieBuilder.Append($"B1SESSION={_config.PreLoginB1SessionId}");
            }
            if (!string.IsNullOrWhiteSpace(_config.RouteIdOverride))
            {
                if (cookieBuilder.Length > 0) cookieBuilder.Append("; ");
                cookieBuilder.Append($"ROUTEID={_config.RouteIdOverride}");
            }
            if (cookieBuilder.Length > 0)
            {
                req.Headers.Add("Cookie", cookieBuilder.ToString());
            }
            return _httpClient.SendAsync(req);
        });
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Login SAP fallito: Status={Status}, Body={Body}", (int)response.StatusCode, Truncate(errorBody));
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Credenziali SAP non valide (401)");
            }
            throw new HttpRequestException($"Login SAP non riuscito. Status={(int)response.StatusCode} Body={Truncate(errorBody)}");
        }

        string sessionId = await ExtractSessionIdAsync(response);
        string? routeId = ExtractRouteId(response);
		if (string.IsNullOrWhiteSpace(sessionId))
		{
			// Alcune versioni ritornano il SessionId nel body
			var json = await response.Content.ReadAsStringAsync();
			if (!string.IsNullOrWhiteSpace(json))
			{
				try
				{
					using var doc = JsonDocument.Parse(json);
					if (doc.RootElement.TryGetProperty("SessionId", out var sidProp))
					{
						sessionId = sidProp.GetString() ?? string.Empty;
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Impossibile parsare SessionId dalla risposta di login");
				}
			}
		}

        if (string.IsNullOrWhiteSpace(sessionId))
		{
			throw new InvalidOperationException("SessionId non ottenuto dal Service Layer");
		}

        if (string.IsNullOrWhiteSpace(routeId) && !string.IsNullOrWhiteSpace(_config.RouteIdOverride))
        {
            routeId = _config.RouteIdOverride;
        }

        _logger.LogInformation("Login SAP riuscito. SessionId: {SessionId} RouteId: {RouteId}", Mask(sessionId), routeId);

        // Set default Cookie header for subsequent requests (align with working implementation)
        try
        {
            var cookieString = string.IsNullOrWhiteSpace(routeId)
                ? $"B1SESSION={sessionId}"
                : $"B1SESSION={sessionId}; ROUTEID={routeId}";
            _httpClient.DefaultRequestHeaders.Remove("Cookie");
            _httpClient.DefaultRequestHeaders.Add("Cookie", cookieString);
        }
        catch
        {
            // ignore header set issues; per-request headers will still be added
        }

        return new SapSession { SessionId = sessionId, RouteId = routeId };
	}

    public async Task<SalesOrder> GetSalesOrderAsync(int docEntry, SapSession session)
	{
        using var response = await _httpRetryPolicy.ExecuteAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"Orders({docEntry})");
            AddAuthCookies(req, session);
            return _httpClient.SendAsync(req);
        });
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			throw new InvalidOperationException($"Ordine non trovato DocEntry={docEntry}");
		}
		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			throw new InvalidOperationException("Sessione SAP non autorizzata (401)");
		}
		response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
		var order = new SalesOrder();
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			order.DocEntry = TryGetInt(root, "DocEntry");
			order.DocNum = TryGetInt(root, "DocNum");
			order.CardCode = TryGetString(root, "CardCode");
			order.CardName = TryGetString(root, "CardName");
            order.Project = TryGetString(root, "Project");

			if (root.TryGetProperty("DocumentLines", out var lines) && lines.ValueKind == JsonValueKind.Array)
			{
				foreach (var line in lines.EnumerateArray())
				{
					var item = new OrderItem
					{
						ItemCode = TryGetString(line, "ItemCode"),
						ItemName = TryGetString(line, "ItemDescription"),
						Quantity = TryGetDecimal(line, "Quantity"),
						LineNum = TryGetInt(line, "LineNum"),
						HasBom = false,
						WarehouseCode = TryGetString(line, "WarehouseCode")
					};
					order.Items.Add(item);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Errore parsing ordine SAP. Body: {Body}", Truncate(json));
			throw;
		}

		_logger.LogInformation("Ordine recuperato: DocNum={DocNum}, Cliente={CardName}", order.DocNum, order.CardName);
		return order;
	}

    public async Task<BillOfMaterial> GetBillOfMaterialAsync(string itemCode, SapSession session)
	{
        // Prefer direct ProductTrees('TreeCode') endpoint as per verified Postman
        var treeCode = Uri.EscapeDataString(itemCode);
        var url = $"ProductTrees('{treeCode}')";
        using var response = await _httpRetryPolicy.ExecuteAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthCookies(req, session);
            return _httpClient.SendAsync(req);
        });
		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			throw new InvalidOperationException("Sessione SAP non autorizzata (401)");
		}
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			// Considera senza distinta
			return new BillOfMaterial { ParentItemCode = itemCode };
		}
		response.EnsureSuccessStatusCode();

		var json = await response.Content.ReadAsStringAsync();
		var bom = new BillOfMaterial { ParentItemCode = itemCode };
		try
		{
			using var doc = JsonDocument.Parse(json);
            var tree = doc.RootElement;
            if (tree.ValueKind == JsonValueKind.Object)
            {
                if (tree.TryGetProperty("ProductTreeLines", out var components) && components.ValueKind == JsonValueKind.Array)
                {
                    foreach (var comp in components.EnumerateArray())
                    {
                        var c = new BomComponent
                        {
                            ItemCode = TryGetString(comp, "ItemCode"),
                            ItemName = TryGetString(comp, "ItemName"),
                            Quantity = TryGetDecimal(comp, "Quantity"),
                            LineNum = TryGetInt(comp, "LineNum")
                        };
                        if (!string.IsNullOrWhiteSpace(c.ItemCode))
                        {
                            bom.Components.Add(c);
                        }
                    }
                }
            }
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Errore parsing BOM per {ItemCode}. Body: {Body}", itemCode, Truncate(json));
			// ritorna vuoto per proseguire
		}

		return bom;
	}

	public async Task LogoutAsync(string sessionId)
	{
		try
		{
            using var response = await _httpRetryPolicy.ExecuteAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "Logout");
                req.Headers.Add("Cookie", $"B1SESSION={sessionId}");
                return _httpClient.SendAsync(req);
            });
			if (!response.IsSuccessStatusCode)
			{
				_logger.LogWarning("Logout SAP ha restituito {StatusCode}", (int)response.StatusCode);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Errore durante logout SAP");
		}
	}

	private static string Mask(string value)
	{
		if (string.IsNullOrEmpty(value)) return value;
		return value.Length <= 4 ? "****" : $"***{value[^3..]}";
	}

	private static int TryGetInt(JsonElement el, string name)
	{
		return el.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;
	}

	private static decimal TryGetDecimal(JsonElement el, string name)
	{
		if (el.TryGetProperty(name, out var p))
		{
			if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v)) return v;
			if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out var s)) return s;
		}
		return 0m;
	}

	private static string TryGetString(JsonElement el, string name)
	{
		return el.TryGetProperty(name, out var p) ? p.ToString() : string.Empty;
	}

	private static string Truncate(string text, int max = 800)
	{
		if (string.IsNullOrEmpty(text)) return text;
		return text.Length <= max ? text : text[..max] + "...";
	}

	private static string EscapeOData(string value)
	{
		return value.Replace("'", "''");
	}

	private static async Task<string> ExtractSessionIdAsync(HttpResponseMessage response)
	{
		// Search Set-Cookie headers for B1SESSION
		if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
		{
			foreach (var cookie in cookies)
			{
				var parts = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				foreach (var part in parts)
				{
					if (part.StartsWith("B1SESSION=", StringComparison.OrdinalIgnoreCase))
					{
						return part.Substring("B1SESSION=".Length);
					}
				}
			}
		}
		// Fallback: read body for SessionId
		try
		{
			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("SessionId", out var sid))
			{
				return sid.GetString() ?? string.Empty;
			}
		}
		catch
		{
			// ignore
		}
		return string.Empty;
	}

    private static string? ExtractRouteId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var parts = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                {
                    if (part.StartsWith("ROUTEID=", StringComparison.OrdinalIgnoreCase))
                    {
                        return part.Substring("ROUTEID=".Length);
                    }
                }
            }
        }
        return null;
    }

    private static void AddAuthCookies(HttpRequestMessage req, SapSession session)
    {
        var cookie = new StringBuilder();
        cookie.Append($"B1SESSION={session.SessionId}");
        if (!string.IsNullOrWhiteSpace(session.RouteId))
        {
            cookie.Append($"; ROUTEID={session.RouteId}");
        }
        req.Headers.Add("Cookie", cookie.ToString());
    }
}
