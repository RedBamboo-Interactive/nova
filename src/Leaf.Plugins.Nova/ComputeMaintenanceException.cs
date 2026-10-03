namespace Leaf.Plugins.Nova;

/// <summary>A definitive creation refusal, not an uncertain message admission.</summary>
public sealed class ComputeMaintenanceException : Exception
{
    public const string ErrorCode = "maintenance_draining";
    public ComputeMaintenanceException() : base(
        "Not sent: an update is waiting to restart Leaf. Your draft is kept on this device. Retry after the update finishes.") { }
}
