using System;

namespace Orders;

/// <summary>Marker attribute for the DI convention under test (spec section 5, P10) — matches codemap.config.json's "diAttribute". Carries no parameters on purpose: the marker itself carries no information, the binding is inferred from which interfaces the type implements.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InjectableAttribute : Attribute
{
}
