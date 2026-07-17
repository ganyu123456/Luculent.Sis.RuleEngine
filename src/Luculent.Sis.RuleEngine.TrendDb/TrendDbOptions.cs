namespace Luculent.Sis.RuleEngine.TrendDb;

public class TrendDbOptions
{
    public const string SectionName = "TrendDb";

    public string ConnectionString { get; set; } = string.Empty;
    public int RealTimePoolSize { get; set; } = 4;
}
