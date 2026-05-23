namespace Nova.App.Configuration;

public class NovaConfig
{
    public int Port { get; set; } = 18803;
    public string WorkspacePath { get; set; } = "";
    public TunnelSettings Tunnel { get; set; } = new();
}

public class TunnelSettings
{
    public bool Enabled { get; set; }
    public string? TunnelToken { get; set; }
    public string? Hostname { get; set; }
    public string? CloudflaredPath { get; set; }
    public string? AccessToken { get; set; }
}
