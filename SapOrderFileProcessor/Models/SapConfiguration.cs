using System.Collections.Generic;

namespace SapOrderFileProcessor.Models;

public sealed class SapConfiguration
{
	public string ServiceLayerUrl { get; set; } = string.Empty;
	public string CompanyDB { get; set; } = string.Empty;
	public string UserName { get; set; } = string.Empty;
	public string Password { get; set; } = string.Empty;
	public string MasterFolder { get; set; } = string.Empty;
	public string DestinationBaseFolder { get; set; } = string.Empty;
	public string FileSearchPattern { get; set; } = "{ItemCode}*.*";
    // Optional cookie override for ROUTEID when sticky sessions are required and not returned at login
    public string? RouteIdOverride { get; set; }
	// Optional cookie to send B1SESSION also on Login when environment requires it
	public string? PreLoginB1SessionId { get; set; }
	// Allow ignoring SSL errors for self-signed Service Layer certificates (use only in trusted networks)
	public bool AllowInsecureSsl { get; set; }
	
	// Client-specific folder mappings
	public Dictionary<string, string> ClientFolderMappings { get; set; } = new();
	
	// Default client folder for clients not in mappings
	public string DefaultClientFolder { get; set; } = "Adotta Italia Srl";
	
	// Folders to search for files (instead of using MasterFolder)
	public List<string> SearchFolders { get; set; } = new();
}
