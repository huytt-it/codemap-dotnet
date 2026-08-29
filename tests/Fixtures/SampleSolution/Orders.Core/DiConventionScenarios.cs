namespace Orders;

/// <summary>Fixture for P10 (spec section 5, "Trích DI theo attribute"): one file per binding outcome the convention must handle.</summary>

// --- implements exactly 1 real interface -> attribute binds IPricingService -> PricingService ---
public interface IPricingService
{
    decimal GetPrice(int orderId);
}

[Injectable]
public class PricingService : IPricingService
{
    public decimal GetPrice(int orderId) => 0m;
}

// --- implements 0 interfaces -> self-registration ---
[Injectable]
public class AuditLogger
{
    public void Log(string message)
    {
    }
}

// --- implements 2 real interfaces -> ambiguous, no guessing ---
public interface IExportable
{
    string Export();
}

[Injectable]
public class ReportGenerator : IPricingService, IExportable
{
    public decimal GetPrice(int orderId) => 0m;
    public string Export() => "";
}

// --- empty marker interface doesn't count as "real" -> still self-registration ---
public interface IMarker
{
}

[Injectable]
public class TaggedOnly : IMarker
{
}

// --- attribute source (INotifier) vs fluent source (NotifierBase, a base CLASS not an interface, so it
// doesn't affect the "exactly 1 real interface" count) disagree on the bound service type -> conflict ---
public abstract class NotifierBase
{
    public abstract void Notify(string message);
}

public interface INotifier
{
    void Notify(string message);
}

[Injectable]
public class EmailNotifier : NotifierBase, INotifier
{
    public override void Notify(string message)
    {
    }
}
