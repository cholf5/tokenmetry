namespace TokenPulse.Core.Models;

public class AnomalyNotice
{
    public string Kind { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal Ratio { get; set; }
}
