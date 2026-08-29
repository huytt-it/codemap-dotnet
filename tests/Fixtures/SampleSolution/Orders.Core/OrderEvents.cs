using System;

namespace Orders;

/// <summary>Fixture for S1: delegates and events were once missed entirely by SyntaxSymbolWalker (found while
/// testing on real nopCommerce/SmartStoreNET — e.g. Nop.Web.Framework.Localization.Localizer.cs had 2 delegates, 0 symbols).</summary>
public delegate void OrderCancelledHandler(int orderId);

public class OrderEvents
{
    public event OrderCancelledHandler? Cancelled;

    public event EventHandler? Refunded
    {
        add { }
        remove { }
    }
}
