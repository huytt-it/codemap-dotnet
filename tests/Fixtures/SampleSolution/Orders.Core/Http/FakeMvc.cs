using System;

namespace Orders.Http;

/// <summary>
/// Stand-in for ASP.NET Core's Controller infrastructure, offline/self-contained like FakeServiceCollection.
/// Entry point detection matches base types/attributes BY NAME (spec section 5, "Entry point"), so these fakes
/// exercise the exact same code path a real Microsoft.AspNetCore.Mvc reference would.
/// </summary>
public abstract class ControllerBase
{
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class RouteAttribute : Attribute
{
    public RouteAttribute(string template) => Template = template;
    public string Template { get; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpGetAttribute : Attribute
{
    public HttpGetAttribute()
    {
    }

    public HttpGetAttribute(string template) => Template = template;
    public string? Template { get; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpDeleteAttribute : Attribute
{
    public HttpDeleteAttribute()
    {
    }

    public HttpDeleteAttribute(string template) => Template = template;
    public string? Template { get; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpPostAttribute : Attribute
{
    public HttpPostAttribute()
    {
    }

    public HttpPostAttribute(string template) => Template = template;
    public string? Template { get; }
}
