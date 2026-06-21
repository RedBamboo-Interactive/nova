namespace Nova.App.Configuration;

public class NovaConfig
{
    public int Port { get; set; } = 18803;
    public string WorkspacePath { get; set; } = "";
    public string? DockerImage { get; set; }

    /// <summary>Quality tier sent to RedCompute when no tier is specified (e.g. new discussions).</summary>
    public string DefaultQualityMode { get; set; } = "standard";

    /// <summary>RedLeaf agent entity ID that this Nova instance represents.</summary>
    public string? AgentId { get; set; }

    public TunnelSettings Tunnel { get; set; } = new();
    public SuiteSettings Suite { get; set; } = new();
}

/// <summary>Base URLs of the sibling Red Suite services.</summary>
public class SuiteSettings
{
    public string RedCompute { get; set; } = "http://localhost:18800";
    public string CodeRed { get; set; } = "http://localhost:18801";
    public string RedMatter { get; set; } = "http://localhost:18802";
    public string RedLeaf { get; set; } = "http://localhost:18804";
}

public class TunnelSettings
{
    public bool Enabled { get; set; }
    public string? TunnelToken { get; set; }
    public string? Hostname { get; set; }
    public string? CloudflaredPath { get; set; }
    public string? AccessToken { get; set; }
}
