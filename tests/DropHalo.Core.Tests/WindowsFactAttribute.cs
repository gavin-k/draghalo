namespace DropHalo.Core.Tests;

/// <summary>A fact that only holds on Windows; elsewhere it is reported as skipped, not as passed.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <param name="only">What only Windows does, given as the reason for skipping elsewhere.</param>
    public WindowsFactAttribute(string only = "Windows file identity only.")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = only;
        }
    }
}
